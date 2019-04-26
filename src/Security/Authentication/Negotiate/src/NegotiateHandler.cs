// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.Authentication.Negotiate
{
    /// <summary>
    /// Authenticates requests using Negotiate, Kerberos, or NTLM.
    /// </summary>
    public class NegotiateHandler : AuthenticationHandler<NegotiateOptions>, IAuthenticationRequestHandler
    {
        private static string NegotiateStateKey = nameof(INegotiateState);
        // TODO: CONFIG?
        private static string _verb = "Negotiate";// "NTLM";
        private static string _prefix = _verb + " ";

        /// <summary>
        /// Creates a new <see cref="NegotiateHandler"/>
        /// </summary>
        /// <param name="options"></param>
        /// <param name="logger"></param>
        /// <param name="encoder"></param>
        /// <param name="clock"></param>
        public NegotiateHandler(IOptionsMonitor<NegotiateOptions> options, ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock)
            : base(options, logger, encoder, clock)
        { }

        /// <summary>
        /// The handler calls methods on the events which give the application control at certain points where processing is occurring. 
        /// If it is not provided a default instance is supplied which does nothing when the methods are called.
        /// </summary>
        protected new NegotiateEvents Events
        {
            get => (NegotiateEvents)base.Events;
            set => base.Events = value;
        }

        /// <summary>
        /// Creates the default events type.
        /// </summary>
        /// <returns></returns>
        protected override Task<object> CreateEventsAsync() => Task.FromResult<object>(new NegotiateEvents());

        private bool IsHttp2 => string.Equals("HTTP/2", Request.Protocol, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Intercepts incomplete Negotiate authentication handshakes and continues or completes them.
        /// </summary>
        /// <returns>True if a response was generated, false otherwise.</returns>
        public async Task<bool> HandleRequestAsync()
        {
            try
            {
                if (IsHttp2)
                {
                    // HTTP/2 is not supported. Do not throw because this may be running on a server that supports
                    // both HTTP/1 and HTTP/2.
                    return false;
                }

                var connectionItems = GetConnectionItems();
                var authState = (INegotiateState)connectionItems[NegotiateStateKey];

                var authorizationHeader = Request.Headers[HeaderNames.Authorization];

                if (StringValues.IsNullOrEmpty(authorizationHeader))
                {
                    if (authState?.IsCompleted == false)
                    {
                        throw new InvalidOperationException("An anonymous request was received in between authentication handshake requests.");
                    }
                    Logger.LogDebug($"No Authorization header");
                    return false;
                }

                var authorization = authorizationHeader.ToString();
                Logger.LogTrace($"Authorization: " + authorization);
                string token = null;
                if (authorization.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
                {
                    token = authorization.Substring(_prefix.Length).Trim();
                }
                else
                {
                    Logger.LogDebug($"Non-Negotiate Authorization header");
                    return false;
                }

                if (authState == null)
                {
                    // TODO: IConnectionLifetimeFeature would fire mid-request.
                    // Replace with IConnectionCompleteFeature.OnCompleted. https://github.com/aspnet/AspNetCore/pull/9754
                    var connectionLifetimeFeature = Context.Features.Get<IConnectionLifetimeFeature>();
                    if (connectionLifetimeFeature == null)
                    {
                        throw new NotSupportedException($"Negotiate authentication requires a server that supports {nameof(IConnectionLifetimeFeature)} like Kestrel.");
                    }
                    connectionItems[NegotiateStateKey] = authState = Options.StateFactory.CreateInstance();
                    connectionLifetimeFeature.ConnectionClosed.UnsafeRegister(DisposeState, authState);
                }

                var outgoing = authState.GetOutgoingBlob(token);
                if (!authState.IsCompleted)
                {
                    Logger.LogInformation($"Incomplete-Negotiate, 401 {_verb} {outgoing}");
                    Response.StatusCode = StatusCodes.Status401Unauthorized;
                    Response.Headers.Append(HeaderNames.WWWAuthenticate, $"{_verb} {outgoing}");

                    // TODO: Consider disposing the authState and caching a copy of the user instead.
                    return true;
                }

                // TODO SPN check? NTLM + CBT only?

                Logger.LogInformation($"Completed-Negotiate, {_verb} {outgoing}");
                if (!string.IsNullOrEmpty(outgoing))
                {
                    // There can be a final blob of data we need to send to the client, but let the request execute as normal.
                    Response.Headers.Append(HeaderNames.WWWAuthenticate, $"{_verb} {outgoing}");
                }
            }
            catch (Exception ex)
            {
                var context = new AuthenticationFailedContext(Context, Scheme, Options) { Exception = ex };
                await Events.AuthenticationFailed(context);
                // TODO: Handled, return true/false or rethrow.
                throw;
            }

            return false;
        }

        /// <summary>
        /// Checks if the current request is authenticated and returns the user.
        /// </summary>
        /// <returns></returns>
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (IsHttp2)
            {
                // Not supported. We don't throw because Negotiate may be set as the default auth
                // handler on a server that's running HTTP/1 and HTTP/2.
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var authState = (INegotiateState)GetConnectionItems()[NegotiateStateKey];
            if (authState != null && authState.IsCompleted)
            {
                Logger.LogDebug($"Cached User");

                // Make a new copy of the user for each request, they are mutable objects
                var identity = authState.GetIdentity();
                ClaimsPrincipal user;
                if (identity is WindowsIdentity winIdentity)
                {
                    user = new WindowsPrincipal(winIdentity);
                    Response.RegisterForDispose(winIdentity);
                }
                else
                {
                    user = new ClaimsPrincipal(new ClaimsIdentity(identity));
                }

                var ticket = new AuthenticationTicket(user, Scheme.Name);
                return Task.FromResult(AuthenticateResult.Success(ticket));
            }

            return Task.FromResult(AuthenticateResult.NoResult());
        }

        /// <summary>
        /// Issues a 401 WWW-Authenticate challenge.
        /// </summary>
        /// <param name="properties"></param>
        /// <returns></returns>
        protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            // TODO: Verify clients will downgrade from HTTP/2 to HTTP/1?
            // Or do we need to send HTTP_1_1_REQUIRED? Or throw here?
            // TODO: Should we invalidate your current auth state?
            var authResult = await HandleAuthenticateOnceSafeAsync();
            var eventContext = new NegotiateChallengeContext(Context, Scheme, Options, properties)
            {
                AuthenticateFailure = authResult?.Failure
            };

            await Events.Challenge(eventContext);
            if (eventContext.Handled)
            {
                return;
            }

            Logger.LogDebug($"Challenged");
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            Response.Headers.Append(HeaderNames.WWWAuthenticate, _verb);
        }

        private IDictionary<object, object> GetConnectionItems()
        {
            var connectionItems = Context.Features.Get<IConnectionItemsFeature>()?.Items;
            if (connectionItems == null)
            {
                throw new NotSupportedException($"Negotiate authentication requires a server that supports {nameof(IConnectionItemsFeature)} like Kestrel.");
            }

            return connectionItems;
        }

        private static void DisposeState(object state)
        {
            ((IDisposable)state).Dispose();
        }
    }
}