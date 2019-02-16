﻿// ------------------------------------------------------------------------------
//
// Copyright (c) Microsoft Corporation.
// All rights reserved.
//
// This code is licensed under the MIT License.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files(the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and / or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions :
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
// ------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client.ApiConfig.Parameters;
using Microsoft.Identity.Client.Core;
using Microsoft.Identity.Client.Exceptions;
using Microsoft.Identity.Client.Http;
using Microsoft.Identity.Client.Internal.Broker;
using Microsoft.Identity.Client.OAuth2;
using Microsoft.Identity.Client.TelemetryCore;
using Microsoft.Identity.Client.UI;
using Microsoft.Identity.Client.Utils;

namespace Microsoft.Identity.Client.Internal.Requests
{
    internal class InteractiveRequest : RequestBase
    {
        private readonly SortedSet<string> _extraScopesToConsent;
        private readonly IWebUI _webUi;
        private AuthorizationResult _authorizationResult;
        private string _codeVerifier;
        private string _state;
        private readonly AcquireTokenInteractiveParameters _interactiveParameters;
        private AuthenticationRequestParameters _authenticationRequestParameters;
        BrokerFactory brokerFactory = new BrokerFactory();
        private IBroker Broker;
        private MsalTokenResponse _msalTokenResponse;

        public InteractiveRequest(
            IServiceBundle serviceBundle,
            AuthenticationRequestParameters authenticationRequestParameters,
            AcquireTokenInteractiveParameters interactiveParameters,
            IWebUI webUi)
            : base(serviceBundle, authenticationRequestParameters, interactiveParameters)
        {
            _authenticationRequestParameters = authenticationRequestParameters;
            _interactiveParameters = interactiveParameters;
            RedirectUriHelper.Validate(authenticationRequestParameters.RedirectUri);
            webUi?.ValidateRedirectUri(authenticationRequestParameters.RedirectUri);

            // todo(migration): can't this just come directly from interactive parameters instead of needing do to this?
            _extraScopesToConsent = new SortedSet<string>();
            if (!_interactiveParameters.ExtraScopesToConsent.IsNullOrEmpty())
            {
                _extraScopesToConsent = ScopeHelper.CreateSortedSetFromEnumerable(_interactiveParameters.ExtraScopesToConsent);
            }

            ValidateScopeInput(_extraScopesToConsent);

            _webUi = webUi;
            _interactiveParameters.LogParameters(_authenticationRequestParameters.RequestContext.Logger);
        }

        protected override void EnrichTelemetryApiEvent(ApiEvent apiEvent)
        {
            apiEvent.Prompt = _interactiveParameters.Prompt.PromptValue;
            if (_interactiveParameters.LoginHint != null)
            {
                apiEvent.LoginHint = _interactiveParameters.LoginHint;
            }
        }

        internal override async Task<AuthenticationResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            await ResolveAuthorityEndpointsAsync().ConfigureAwait(false);

            await CheckForBrokerAndAcquireAuthorizationAsync(cancellationToken).ConfigureAwait(false);

            return CacheTokenResponseAndCreateAuthenticationResult(_msalTokenResponse);
        }

        private async Task AcquireAuthorizationAsync()
        {
            var authorizationUri = CreateAuthorizationUri(true, true);

            var uiEvent = new UiEvent();
            using (ServiceBundle.TelemetryManager.CreateTelemetryHelper(
                AuthenticationRequestParameters.RequestContext.TelemetryRequestId,
                AuthenticationRequestParameters.ClientId,
                uiEvent))
            {
                _authorizationResult = await _webUi.AcquireAuthorizationAsync(
                                           authorizationUri,
                                           AuthenticationRequestParameters.RedirectUri,
                                           AuthenticationRequestParameters.RequestContext).ConfigureAwait(false);
                uiEvent.UserCancelled = _authorizationResult.Status == AuthorizationStatus.UserCancel;
                uiEvent.AccessDenied = _authorizationResult.Status == AuthorizationStatus.ProtocolError;
            }
        }

        internal async Task<Uri> CreateAuthorizationUriAsync()
        {
            await ResolveAuthorityEndpointsAsync().ConfigureAwait(false);
            return CreateAuthorizationUri();
        }

        private Dictionary<string, string> GetBodyParameters()
        {
            var dict = new Dictionary<string, string>
            {
                [OAuth2Parameter.GrantType] = OAuth2GrantType.AuthorizationCode,
                [OAuth2Parameter.Code] = _authorizationResult.Code,
                [OAuth2Parameter.RedirectUri] = AuthenticationRequestParameters.RedirectUri.OriginalString,
                [OAuth2Parameter.CodeVerifier] = _codeVerifier
            };

            return dict;
        }

        private Uri CreateAuthorizationUri(bool addVerifier = false, bool addState = false)
        {
            IDictionary<string, string> requestParameters = CreateAuthorizationRequestParameters();

            if (addVerifier)
            {
                _codeVerifier = ServiceBundle.PlatformProxy.CryptographyManager.GenerateCodeVerifier();
                string codeVerifierHash = ServiceBundle.PlatformProxy.CryptographyManager.CreateBase64UrlEncodedSha256Hash(_codeVerifier);

                requestParameters[OAuth2Parameter.CodeChallenge] = codeVerifierHash;
                requestParameters[OAuth2Parameter.CodeChallengeMethod] = OAuth2Value.CodeChallengeMethodValue;
            }

            if (addState)
            {
                _state = Guid.NewGuid().ToString() + Guid.NewGuid().ToString();
                requestParameters[OAuth2Parameter.State] = _state;
            }

            // Add uid/utid values to QP if user object was passed in.
            if (_interactiveParameters.Account != null)
            {
                if (!string.IsNullOrEmpty(_interactiveParameters.Account.Username))
                {
                    requestParameters[OAuth2Parameter.LoginHint] = _interactiveParameters.Account.Username;
                }

                if (_interactiveParameters.Account?.HomeAccountId?.ObjectId != null)
                {
                    requestParameters[OAuth2Parameter.LoginReq] =
                        _interactiveParameters.Account.HomeAccountId.ObjectId;
                }

                if (!string.IsNullOrEmpty(_interactiveParameters.Account?.HomeAccountId?.TenantId))
                {
                    requestParameters[OAuth2Parameter.DomainReq] =
                        _interactiveParameters.Account.HomeAccountId.TenantId;
                }
            }

            CheckForDuplicateQueryParameters(AuthenticationRequestParameters.ExtraQueryParameters, requestParameters);

            string qp = requestParameters.ToQueryParameter();
            var builder = new UriBuilder(new Uri(AuthenticationRequestParameters.Endpoints.AuthorizationEndpoint));
            builder.AppendQueryParameters(qp);

            return builder.Uri;
        }

        private static void CheckForDuplicateQueryParameters(
            IDictionary<string, string> queryParamsDictionary,
            IDictionary<string, string> requestParameters)
        {
            foreach (KeyValuePair<string, string> kvp in queryParamsDictionary)
            {
                if (requestParameters.ContainsKey(kvp.Key))
                {
                    throw new MsalClientException(
                        MsalClientException.DuplicateQueryParameterError,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            MsalErrorMessage.DuplicateQueryParameterTemplate,
                            kvp.Key));
                }

                requestParameters[kvp.Key] = kvp.Value;
            }
        }

        private Dictionary<string, string> CreateAuthorizationRequestParameters()
        {
            SortedSet<string> unionScope = GetDecoratedScope(
                new SortedSet<string>(AuthenticationRequestParameters.Scope.Union(_extraScopesToConsent)));

            var authorizationRequestParameters = new Dictionary<string, string>
            {
                [OAuth2Parameter.Scope] = unionScope.AsSingleString(),
                [OAuth2Parameter.ResponseType] = OAuth2ResponseType.Code,

                [OAuth2Parameter.ClientId] = AuthenticationRequestParameters.ClientId,
                [OAuth2Parameter.RedirectUri] = AuthenticationRequestParameters.RedirectUri.OriginalString
            };

            if (!string.IsNullOrWhiteSpace(_interactiveParameters.LoginHint))
            {
                authorizationRequestParameters[OAuth2Parameter.LoginHint] = _interactiveParameters.LoginHint;
            }

            if (AuthenticationRequestParameters.RequestContext?.Logger?.CorrelationId != Guid.Empty)
            {
                authorizationRequestParameters[OAuth2Parameter.CorrelationId] =
                    AuthenticationRequestParameters.RequestContext.Logger.CorrelationId.ToString();
            }

            foreach (KeyValuePair<string, string> kvp in MsalIdHelper.GetMsalIdParameters(AuthenticationRequestParameters.RequestContext.Logger))
            {
                authorizationRequestParameters[kvp.Key] = kvp.Value;
            }

            if (_interactiveParameters.Prompt.PromptValue != Prompt.NoPrompt.PromptValue)
            {
                authorizationRequestParameters[OAuth2Parameter.Prompt] = _interactiveParameters.Prompt.PromptValue;
            }

            return authorizationRequestParameters;
        }

        private void VerifyAuthorizationResult()
        {
            if (_authorizationResult.Status == AuthorizationStatus.Success && !_state.Equals(
                    _authorizationResult.State,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new MsalClientException(
                    MsalClientException.StateMismatchError,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Returned state({0}) from authorize endpoint is not the same as the one sent({1})",
                        _authorizationResult.State,
                        _state));
            }

            if (_authorizationResult.Error == OAuth2Error.LoginRequired)
            {
                throw new MsalUiRequiredException(
                    MsalUiRequiredException.NoPromptFailedError,
                    MsalErrorMessage.NoPromptFailedErrorMessage);
            }

            if (_authorizationResult.Status == AuthorizationStatus.UserCancel)
            {
                throw new MsalClientException(_authorizationResult.Error, _authorizationResult.ErrorDescription);
            }

            if (_authorizationResult.Status != AuthorizationStatus.Success)
            {
                throw new MsalServiceException(_authorizationResult.Error, _authorizationResult.ErrorDescription, null);
            }
        }

        private async Task CheckForBrokerAndAcquireAuthorizationAsync(CancellationToken cancellationToken)
        {
            Broker = brokerFactory.CreateBrokerFacade(ServiceBundle);

            if (Broker.CanInvokeBroker(_interactiveParameters.UiParent))
            {
                _msalTokenResponse = await Broker.AcquireTokenUsingBrokerAsync(
                    _authenticationRequestParameters.CreateRequestParametersForBroker()).ConfigureAwait(false);
            }
            else
            {
                await AcquireAuthorizationAsync().ConfigureAwait(false);
                VerifyAuthorizationResult();
                _msalTokenResponse = await SendTokenRequestAsync(GetBodyParameters(), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}