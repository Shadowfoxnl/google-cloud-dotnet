﻿// Copyright 2018 Google LLC
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     https://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Google.Api.Gax;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Google.Cloud.Storage.V1
{
    public sealed partial class UrlSigner
    {
        private sealed class V4Signer : ISigner
        {
            // Note: It's irritating to have to convert from base64 to bytes and then to hex, but we can't change the IBlobSigner implementation
            // and ServiceAccountCredential.CreateSignature returns base64 anyway.

            public string Sign(
                string bucket,
                string objectName,
                DateTimeOffset expiration,
                HttpMethod requestMethod,
                Dictionary<string, IEnumerable<string>> requestHeaders,
                Dictionary<string, IEnumerable<string>> contentHeaders,
                IBlobSigner blobSigner,
                IClock clock)
            {
                var state = new SigningState(bucket, objectName, expiration, requestMethod, requestHeaders, contentHeaders, blobSigner, clock);
                var base64Signature = blobSigner.CreateSignature(state.blobToSign);
                var rawSignature = Convert.FromBase64String(base64Signature);
                var hexSignature = FormatHex(rawSignature);
                return state.GetResult(hexSignature);
            }

            public async Task<string> SignAsync(
                string bucket,
                string objectName,
                DateTimeOffset expiration,
                HttpMethod requestMethod,
                Dictionary<string, IEnumerable<string>> requestHeaders,
                Dictionary<string, IEnumerable<string>> contentHeaders,
                IBlobSigner blobSigner,
                IClock clock,
                CancellationToken cancellationToken)
            {
                var state = new SigningState(bucket, objectName, expiration, requestMethod, requestHeaders, contentHeaders, blobSigner, clock);
                var base64Signature = await blobSigner.CreateSignatureAsync(state.blobToSign, cancellationToken).ConfigureAwait(false);
                var rawSignature = Convert.FromBase64String(base64Signature);
                var hexSignature = FormatHex(rawSignature);
                return state.GetResult(hexSignature);
            }

            /// <summary>
            /// State which needs to be carried between the "pre-signing" stage and "post-signing" stages
            /// of the implementation.
            /// </summary>
            private struct SigningState
            {
                private string resourcePath;
                private List<string> queryParameters;
                internal byte[] blobToSign;

                internal SigningState(
                    string bucket,
                    string objectName,
                    DateTimeOffset expiration,
                    HttpMethod requestMethod,
                    Dictionary<string, IEnumerable<string>> requestHeaders,
                    Dictionary<string, IEnumerable<string>> contentHeaders,
                    IBlobSigner blobSigner,
                    IClock clock)
                {
                    StorageClientImpl.ValidateBucketName(bucket);

                    bool isResumableUpload = false;
                    if (requestMethod == null)
                    {
                        requestMethod = HttpMethod.Get;
                    }
                    else if (requestMethod == ResumableHttpMethod)
                    {
                        isResumableUpload = true;
                        requestMethod = HttpMethod.Post;
                    }

                    var now = clock.GetCurrentDateTimeUtc();
                    var timestamp = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
                    var datestamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                    // TODO: Validate again maximum expirary duration
                    int expirySeconds = (int) (expiration - now).TotalSeconds;
                    string expiryText = expirySeconds.ToString(CultureInfo.InvariantCulture);

                    string clientEmail = blobSigner.Id;
                    string credentialScope = $"{datestamp}/auto/gcs/goog4_request";
                    string credential = WebUtility.UrlEncode($"{blobSigner.Id}/{credentialScope}");

                    // FIXME: Use requestHeaders and contentHeaders
                    var headers = new SortedDictionary<string, string>();
                    headers["host"] = "storage.googleapis.com";

                    var canonicalHeaderBuilder = new StringBuilder();
                    foreach (var pair in headers)
                    {
                        canonicalHeaderBuilder.Append($"{pair.Key}:{pair.Value}\n");
                    }

                    var canonicalHeaders = canonicalHeaderBuilder.ToString().ToLowerInvariant();
                    var signedHeaders = string.Join(";", headers.Keys.Select(k => k.ToLowerInvariant()));

                    queryParameters = new List<string>
                    {
                        "x-goog-algorithm=GOOG4-RSA-SHA256",
                        $"x-goog-credential={credential}",
                        $"x-goog-date={timestamp}",
                        $"x-goog-expires={expirySeconds}",
                        $"x-goog-signedheaders={signedHeaders}"
                    };
                    if (isResumableUpload)
                    {
                        queryParameters.Insert(4, "X-Goog-Resumable=Start");
                    }

                    var canonicalQueryString = string.Join("&", queryParameters);
                    resourcePath = $"/{bucket}";
                    if (objectName != null)
                    {
                        resourcePath += $"/{Uri.EscapeDataString(objectName)}";
                    }

                    var canonicalRequest = $"{requestMethod}\n{resourcePath}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\nUNSIGNED-PAYLOAD";
                    string hashHex;
                    using (var sha256 = SHA256.Create())
                    {
                        hashHex = FormatHex(sha256.ComputeHash(Encoding.UTF8.GetBytes(canonicalRequest)));
                    }

                    blobToSign = Encoding.UTF8.GetBytes($"GOOG4-RSA-SHA256\n{timestamp}\n{credentialScope}\n{hashHex}");
                }

                internal string GetResult(string signature)
                {
                    queryParameters.Add($"x-goog-signature={WebUtility.UrlEncode(signature)}");
                    return $"{StorageHost}{resourcePath}?{string.Join("&", queryParameters)}";
                }
            }

            private const string HexCharacters = "0123456789abcdef";
            private static string FormatHex(byte[] bytes)
            {
                // Could just use BitConverter, but it's inefficient to create multiple strings and
                // easy to do it ourselves instead.
                char[] chars = new char[bytes.Length * 2];
                for (int i = 0; i < bytes.Length; i++)
                {
                    chars[i * 2] = HexCharacters[bytes[i] >> 4];
                    chars[i * 2 + 1] = HexCharacters[bytes[i] & 0xf];
                }
                return new string(chars);
            }
        }
    }
}
