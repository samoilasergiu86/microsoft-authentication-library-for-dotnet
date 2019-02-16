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

using Microsoft.Identity.Client.Core;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client.Utils;
using UIKit;
using Foundation;
using System;
using CoreFoundation;
using Microsoft.Identity.Client.OAuth2;
using Microsoft.Identity.Client.Internal.Broker;
using Microsoft.Identity.Client.UI;
using Microsoft.Identity.Client.AppConfig;
using Microsoft.Identity.Client.Exceptions;
using Microsoft.Identity.Client.ApiConfig;

namespace Microsoft.Identity.Client.Platforms.iOS
{
    /// <summary>
    /// Handles requests which invoke the broker. This is only for mobile (iOS and Android) scenarios.
    /// </summary>
    internal class iOSBroker : NSObject, IBroker
    {
        private static SemaphoreSlim brokerResponseReady = null;
        private static NSUrl brokerResponse = null;

        private Dictionary<string, string> _brokerPayload;

        private IServiceBundle _serviceBundle;

        public iOSBroker(IServiceBundle serviceBundle)
        {
            _serviceBundle = serviceBundle;
        }

        public bool CanInvokeBroker(OwnerUiParent uiParent)
        {
            if (uiParent == null)
            {
                _serviceBundle.DefaultLogger.Verbose(iOSBrokerConstants.UiParentIsNullCannotInvokeBroker);
                return false;
            }

            if (uiParent.CoreUiParent.CallerViewController == null)
            {
                _serviceBundle.DefaultLogger.Verbose(iOSBrokerConstants.CallerViewControllerIsNullCannotInvokeBroker);
                return false;
            }

            var result = false;

            if (!_serviceBundle.Config.IsBrokerEnabled)
            {
                return result;
            }
            else
            {
                _serviceBundle.DefaultLogger.Verbose(iOSBrokerConstants.CanInvokeBroker + _serviceBundle.Config.IsBrokerEnabled);

                uiParent.CoreUiParent.CallerViewController.InvokeOnMainThread(() =>
                {
                    result = UIApplication.SharedApplication.CanOpenUrl(new NSUrl("msauthv2://"));
                });
            }
            if (!result)
            {
                _serviceBundle.DefaultLogger.Verbose(result + iOSBrokerConstants.CanInvokeBrokerReturnsFalseMessage);
            }

            return result;
        }

        public async Task<MsalTokenResponse> AcquireTokenUsingBrokerAsync(Dictionary<string, string> brokerPayload)
        {
            _brokerPayload = brokerPayload;

            AddIosSpecificParametersToPayload();

            await InvokeIosBrokerAsync().ConfigureAwait(false);

            return ProcessBrokerResponse();
        }

        private void AddIosSpecificParametersToPayload()
        {
            string encodedBrokerKey = Base64UrlHelpers.Encode(BrokerKeyHelper.GetRawBrokerKey(_serviceBundle.DefaultLogger));
            _brokerPayload[iOSBrokerConstants.BrokerKey] = encodedBrokerKey;
            _brokerPayload[iOSBrokerConstants.MsgProtocolVer] = BrokerParameter.MsgProtocolVersion3;

            if (_brokerPayload.ContainsKey(iOSBrokerConstants.Claims))
            {
                _brokerPayload.Add(iOSBrokerConstants.SkipCache, BrokerParameter.SkipCache);
                string claims = Base64UrlHelpers.Encode(_brokerPayload[BrokerParameter.Claims]);
                _brokerPayload[BrokerParameter.Claims] = claims;
            }
        }

        private async Task InvokeIosBrokerAsync()
        {
            brokerResponseReady = new SemaphoreSlim(0);

            if (_brokerPayload.ContainsKey(BrokerParameter.BrokerInstallUrl))
            {
                 _serviceBundle.DefaultLogger.Info(iOSBrokerConstants.BrokerPayloadContainsInstallUrl);

                string url = _brokerPayload[BrokerParameter.BrokerInstallUrl];
                Uri uri = new Uri(url);
                string query = uri.Query;

                if (query.StartsWith("?", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Substring(1);
                }

                _serviceBundle.DefaultLogger.Info(iOSBrokerConstants.InvokeIosBrokerAppLink);

                Dictionary<string, string> keyPair = CoreHelpers.ParseKeyValueList(query, '&', true, false, null);

                _serviceBundle.DefaultLogger.Info(iOSBrokerConstants.StartingActionViewActivity + iOSBrokerConstants.AppLink);

                DispatchQueue.MainQueue.DispatchAsync(() => UIApplication.SharedApplication.OpenUrl(new NSUrl(keyPair[iOSBrokerConstants.AppLink])));

                throw new MsalClientException(MsalErrorIOSEx.BrokerApplicationRequired, MsalErrorMessageIOSEx.BrokerApplicationRequired);
            }

            else
            {
                _serviceBundle.DefaultLogger.Info(iOSBrokerConstants.InvokeTheIosBroker);

                NSUrl url = new NSUrl(iOSBrokerConstants.InvokeBroker + _brokerPayload.ToQueryParameter());

                _serviceBundle.DefaultLogger.VerbosePii(iOSBrokerConstants.BrokerPayloadPii + _brokerPayload.ToQueryParameter(),

                iOSBrokerConstants.BrokerPayloadNoPii + _brokerPayload.Count);

                DispatchQueue.MainQueue.DispatchAsync(() => UIApplication.SharedApplication.OpenUrl(url));
            }

            await brokerResponseReady.WaitAsync().ConfigureAwait(false);
        }

        private MsalTokenResponse ProcessBrokerResponse()
        {
            string[] keyValuePairs = brokerResponse.Query.Split('&');
            Dictionary<string, string> responseDictionary = new Dictionary<string, string>();

            foreach (string pair in keyValuePairs)
            {
                string[] keyValue = pair.Split('=');
                responseDictionary[keyValue[0]] = CoreHelpers.UrlDecode(keyValue[1]);

                if (responseDictionary[keyValue[0]].Equals("(null)", StringComparison.OrdinalIgnoreCase)
                    && keyValue[0].Equals(iOSBrokerConstants.Code, StringComparison.OrdinalIgnoreCase))
                {
                    responseDictionary[iOSBrokerConstants.Error] = iOSBrokerConstants.BrokerError;

                    _serviceBundle.DefaultLogger.VerbosePii(iOSBrokerConstants.BrokerResponseValuesPii + keyValue.ToString(),

                    iOSBrokerConstants.BrokerResponseContainsError);
                }
            }

            _serviceBundle.DefaultLogger.Verbose(iOSBrokerConstants.ProcessBrokerResponse + responseDictionary.Count);

            return ResultFromBrokerResponse(responseDictionary);
        }

        private MsalTokenResponse ResultFromBrokerResponse(Dictionary<string, string> responseDictionary)
        {
            MsalTokenResponse brokerTokenResponse;

            if (responseDictionary.ContainsKey(iOSBrokerConstants.Error) || responseDictionary.ContainsKey(iOSBrokerConstants.ErrorDescription))
            {
                brokerTokenResponse = MsalTokenResponse.CreateFromBrokerResponse(responseDictionary);
            }
            else
            {
                string expectedHash = responseDictionary[iOSBrokerConstants.ExpectedHash];
                string encryptedResponse = responseDictionary[iOSBrokerConstants.EncryptedResponsed];
                string decryptedResponse = BrokerKeyHelper.DecryptBrokerResponse(encryptedResponse, _serviceBundle.DefaultLogger);
                string responseActualHash = _serviceBundle.PlatformProxy.CryptographyManager.CreateSha256Hash(decryptedResponse);
                byte[] rawHash = Convert.FromBase64String(responseActualHash);
                string hash = BitConverter.ToString(rawHash);

                if (expectedHash.Equals(hash.Replace("-", ""), StringComparison.OrdinalIgnoreCase))
                {
                    responseDictionary = CoreHelpers.ParseKeyValueList(decryptedResponse, '&', false, null);
                    brokerTokenResponse = MsalTokenResponse.CreateFromBrokerResponse(responseDictionary);
                }
                else
                {
                    brokerTokenResponse = new MsalTokenResponse
                    {
                        Error = MsalError.BrokerReponseHashMismatch,
                        ErrorDescription = MsalClientException.BrokerReponseHashMismatch
                    };
                }
            }

            return brokerTokenResponse;
        }

        public static void SetBrokerResponse(NSUrl responseUrl)
        {
            brokerResponse = responseUrl;
            brokerResponseReady.Release();
        }
    }
}