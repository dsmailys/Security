﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace Microsoft.AspNetCore.Authentication.WsFederation
{
    public class WsFederationTest
    {
        [Fact]
        public async Task VerifySchemeDefaults()
        {
            var services = new ServiceCollection();
            services.AddAuthentication().AddWsFederation();
            var sp = services.BuildServiceProvider();
            var schemeProvider = sp.GetRequiredService<IAuthenticationSchemeProvider>();
            var scheme = await schemeProvider.GetSchemeAsync(WsFederationDefaults.AuthenticationScheme);
            Assert.NotNull(scheme);
            Assert.Equal("WsFederationHandler", scheme.HandlerType.Name);
            Assert.Equal(WsFederationDefaults.AuthenticationScheme, scheme.DisplayName);
        }

        [Fact]
        public async Task MissingConfigurationThrows()
        {
            var builder = new WebHostBuilder()
                .Configure(ConfigureApp)
                .ConfigureServices(services =>
                {
                    services.AddAuthentication(sharedOptions =>
                    {
                        sharedOptions.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                        sharedOptions.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                        sharedOptions.DefaultChallengeScheme = WsFederationDefaults.AuthenticationScheme;
                    })
                    .AddCookie()
                    .AddWsFederation();
                });
            var server = new TestServer(builder);
            var httpClient = server.CreateClient();

            // Verify if the request is redirected to STS with right parameters
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => httpClient.GetAsync("/"));
            Assert.Equal("Provide MetadataAddress, Configuration, or ConfigurationManager to WsFederationOptions", exception.Message);
        }

        [Fact]
        public async Task ChallengeRedirects()
        {
            var httpClient = CreateClient();

            // Verify if the request is redirected to STS with right parameters
            var response = await httpClient.GetAsync("/");
            Assert.Equal("https://login.windows.net/4afbc689-805b-48cf-a24c-d4aa3248a248/wsfed", response.Headers.Location.GetLeftPart(System.UriPartial.Path));
            var queryItems = QueryHelpers.ParseQuery(response.Headers.Location.Query);

            Assert.Equal("http://Automation1", queryItems["wtrealm"]);
            Assert.True(queryItems["wctx"].ToString().Equals(CustomStateDataFormat.ValidStateData), "wctx does not equal ValidStateData");
            Assert.Equal(httpClient.BaseAddress + "signin-wsfed", queryItems["wreply"]);
            Assert.Equal("wsignin1.0", queryItems["wa"]);
        }

        [Fact]
        public async Task MapWillNotAffectRedirect()
        {
            var httpClient = CreateClient();

            // Verify if the request is redirected to STS with right parameters
            var response = await httpClient.GetAsync("/mapped-challenge");
            Assert.Equal("https://login.windows.net/4afbc689-805b-48cf-a24c-d4aa3248a248/wsfed", response.Headers.Location.GetLeftPart(System.UriPartial.Path));
            var queryItems = QueryHelpers.ParseQuery(response.Headers.Location.Query);

            Assert.Equal("http://Automation1", queryItems["wtrealm"]);
            Assert.True(queryItems["wctx"].ToString().Equals(CustomStateDataFormat.ValidStateData), "wctx does not equal ValidStateData");
            Assert.Equal(httpClient.BaseAddress + "signin-wsfed", queryItems["wreply"]);
            Assert.Equal("wsignin1.0", queryItems["wa"]);
        }

        [Fact]
        public async Task PreMappedWillAffectRedirect()
        {
            var httpClient = CreateClient();

            // Verify if the request is redirected to STS with right parameters
            var response = await httpClient.GetAsync("/premapped-challenge");
            Assert.Equal("https://login.windows.net/4afbc689-805b-48cf-a24c-d4aa3248a248/wsfed", response.Headers.Location.GetLeftPart(System.UriPartial.Path));
            var queryItems = QueryHelpers.ParseQuery(response.Headers.Location.Query);

            Assert.Equal("http://Automation1", queryItems["wtrealm"]);
            Assert.True(queryItems["wctx"].ToString().Equals(CustomStateDataFormat.ValidStateData), "wctx does not equal ValidStateData");
            Assert.Equal(httpClient.BaseAddress + "premapped-challenge/signin-wsfed", queryItems["wreply"]);
            Assert.Equal("wsignin1.0", queryItems["wa"]);
        }

        [Fact]
        public async Task ValidTokenIsAccepted()
        {
            var httpClient = CreateClient();

            // Verify if the request is redirected to STS with right parameters
            var response = await httpClient.GetAsync("/");
            var queryItems = QueryHelpers.ParseQuery(response.Headers.Location.Query);

            var request = new HttpRequestMessage(HttpMethod.Post, queryItems["wreply"]);
            CopyCookies(response, request);
            request.Content = CreateSignInContent("WsFederation/ValidToken.xml", queryItems["wctx"]);
            response = await httpClient.SendAsync(request);

            Assert.Equal(HttpStatusCode.Found, response.StatusCode);

            request = new HttpRequestMessage(HttpMethod.Get, response.Headers.Location);
            CopyCookies(response, request);
            response = await httpClient.SendAsync(request);

            // Did the request end in the actual resource requested for
            Assert.Equal(WsFederationDefaults.AuthenticationScheme, await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task ValidTokenIsSavedInAuthenticationProperties()
        {
            var httpClient = CreateClient(saveTokens: true);

            // Verify if the request is redirected to STS with right parameters
            var response = await httpClient.GetAsync("/");
            var queryItems = QueryHelpers.ParseQuery(response.Headers.Location.Query);

            var request = new HttpRequestMessage(HttpMethod.Post, queryItems["wreply"]);
            CopyCookies(response, request);
            request.Content = CreateSignInContent("WsFederation/ValidToken.xml", queryItems["wctx"]);
            response = await httpClient.SendAsync(request);

            Assert.Equal(HttpStatusCode.Found, response.StatusCode);

            request = new HttpRequestMessage(HttpMethod.Get, response.Headers.Location);
            CopyCookies(response, request);
            response = await httpClient.SendAsync(request);

            // Did the request end in the actual resource requested for
            Assert.Equal(File.ReadAllText("WsFederation/ValidToken.xml"), await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task ValidUnsolicitedTokenIsRefused()
        {
            var httpClient = CreateClient();
            var form = CreateSignInContent("WsFederation/ValidToken.xml", suppressWctx: true);
            var exception = await Assert.ThrowsAsync<Exception>(() => httpClient.PostAsync(httpClient.BaseAddress + "signin-wsfed", form));
            Assert.Contains("Unsolicited logins are not allowed.", exception.InnerException.Message);
        }

        [Fact]
        public async Task ValidUnsolicitedTokenIsAcceptedWhenAllowed()
        {
            var httpClient = CreateClient(allowUnsolicited: true);

            var form = CreateSignInContent("WsFederation/ValidToken.xml", suppressWctx: true);
            var response = await httpClient.PostAsync(httpClient.BaseAddress + "signin-wsfed", form);

            Assert.Equal(HttpStatusCode.Found, response.StatusCode);

            var request = new HttpRequestMessage(HttpMethod.Get, response.Headers.Location);
            CopyCookies(response, request);
            response = await httpClient.SendAsync(request);

            // Did the request end in the actual resource requested for
            Assert.Equal(WsFederationDefaults.AuthenticationScheme, await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task InvalidTokenIsRejected()
        {
            var httpClient = CreateClient();

            // Verify if the request is redirected to STS with right parameters
            var response = await httpClient.GetAsync("/");
            var queryItems = QueryHelpers.ParseQuery(response.Headers.Location.Query);

            var request = new HttpRequestMessage(HttpMethod.Post, queryItems["wreply"]);
            CopyCookies(response, request);
            request.Content = CreateSignInContent("WsFederation/InvalidToken.xml", queryItems["wctx"]);
            response = await httpClient.SendAsync(request);

            // Did the request end in the actual resource requested for
            Assert.Equal("AuthenticationFailed", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task RemoteSignoutRequestTriggersSignout()
        {
            var httpClient = CreateClient();

            var response = await httpClient.GetAsync("/signin-wsfed?wa=wsignoutcleanup1.0");
            response.EnsureSuccessStatusCode();

            var cookie = response.Headers.GetValues(HeaderNames.SetCookie).Single();
            Assert.Equal(".AspNetCore.Cookies=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/; samesite=lax", cookie);
            Assert.Equal("OnRemoteSignOut", response.Headers.GetValues("EventHeader").Single());
            Assert.Equal("", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task EventsResolvedFromDI()
        {
            var builder = new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<MyWsFedEvents>();
                    services.AddAuthentication(sharedOptions =>
                    {
                        sharedOptions.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                        sharedOptions.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                        sharedOptions.DefaultChallengeScheme = WsFederationDefaults.AuthenticationScheme;
                    })
                    .AddCookie()
                    .AddWsFederation(options =>
                    {
                        options.Wtrealm = "http://Automation1";
                        options.MetadataAddress = "https://login.windows.net/4afbc689-805b-48cf-a24c-d4aa3248a248/federationmetadata/2007-06/federationmetadata.xml";
                        options.BackchannelHttpHandler = new WaadMetadataDocumentHandler();
                        options.EventsType = typeof(MyWsFedEvents);
                    });
                })
                .Configure(app =>
                {
                    app.Run(context => context.ChallengeAsync());
                });
            var server = new TestServer(builder);

            var result = await server.CreateClient().GetAsync("");
            Assert.Contains("CustomKey=CustomValue", result.Headers.Location.Query);
        }

        private class MyWsFedEvents : WsFederationEvents
        {
            public override Task RedirectToIdentityProvider(RedirectContext context)
            {
                context.ProtocolMessage.SetParameter("CustomKey", "CustomValue");
                return base.RedirectToIdentityProvider(context);
            }
        }

        private FormUrlEncodedContent CreateSignInContent(string tokenFile, string wctx = null, bool suppressWctx = false)
        {
            var kvps = new List<KeyValuePair<string, string>>();
            kvps.Add(new KeyValuePair<string, string>("wa", "wsignin1.0"));
            kvps.Add(new KeyValuePair<string, string>("wresult", File.ReadAllText(tokenFile)));
            if (!string.IsNullOrEmpty(wctx))
            {
                kvps.Add(new KeyValuePair<string, string>("wctx", wctx));
            }
            if (suppressWctx)
            {
                kvps.Add(new KeyValuePair<string, string>("suppressWctx", "true"));
            }
            return new FormUrlEncodedContent(kvps);
        }

        private void CopyCookies(HttpResponseMessage response, HttpRequestMessage request)
        {
            var cookies = SetCookieHeaderValue.ParseList(response.Headers.GetValues(HeaderNames.SetCookie).ToList());
            foreach (var cookie in cookies)
            {
                if (cookie.Value.HasValue)
                {
                    request.Headers.Add(HeaderNames.Cookie, new CookieHeaderValue(cookie.Name, cookie.Value).ToString());
                }
            }
        }

        private HttpClient CreateClient(bool allowUnsolicited = false, bool saveTokens = false)
        {
            var builder = new WebHostBuilder()
                .Configure(ConfigureApp)
                .ConfigureServices(services =>
                {
                    services.AddAuthentication(sharedOptions =>
                    {
                        sharedOptions.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                        sharedOptions.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                        sharedOptions.DefaultChallengeScheme = WsFederationDefaults.AuthenticationScheme;
                    })
                    .AddCookie()
                    .AddWsFederation(options =>
                    {
                        options.Wtrealm = "http://Automation1";
                        options.MetadataAddress = "https://login.windows.net/4afbc689-805b-48cf-a24c-d4aa3248a248/federationmetadata/2007-06/federationmetadata.xml";
                        options.BackchannelHttpHandler = new WaadMetadataDocumentHandler();
                        options.StateDataFormat = new CustomStateDataFormat();
                        options.SecurityTokenHandlers = new List<ISecurityTokenValidator>() { new TestSecurityTokenValidator() };
                        options.UseTokenLifetime = false;
                        options.AllowUnsolicitedLogins = allowUnsolicited;
                        options.SaveTokens = saveTokens;
                        options.SavedTokenName = "wsfed_token";
                        options.Events = new WsFederationEvents()
                        {
                            OnMessageReceived = context =>
                            {
                                if (!context.ProtocolMessage.Parameters.TryGetValue("suppressWctx", out var suppress))
                                {
                                    Assert.True(context.ProtocolMessage.Wctx.Equals("customValue"), "wctx is not my custom value");
                                }
                                context.HttpContext.Items["MessageReceived"] = true;
                                return Task.FromResult(0);
                            },
                            OnRedirectToIdentityProvider = context =>
                            {
                                if (context.ProtocolMessage.IsSignInMessage)
                                {
                                    // Sign in message
                                    context.ProtocolMessage.Wctx = "customValue";
                                }

                                return Task.FromResult(0);
                            },
                            OnSecurityTokenReceived = context =>
                            {
                                context.HttpContext.Items["SecurityTokenReceived"] = true;
                                return Task.FromResult(0);
                            },
                            OnSecurityTokenValidated = context =>
                            {
                                Assert.True((bool)context.HttpContext.Items["MessageReceived"], "MessageReceived notification not invoked");
                                Assert.True((bool)context.HttpContext.Items["SecurityTokenReceived"], "SecurityTokenReceived notification not invoked");

                                if (context.Principal != null)
                                {
                                    var identity = context.Principal.Identities.Single();
                                    identity.AddClaim(new Claim("ReturnEndpoint", "true"));
                                    identity.AddClaim(new Claim("Authenticated", "true"));
                                    identity.AddClaim(new Claim(identity.RoleClaimType, "Guest", ClaimValueTypes.String));
                                }

                                return Task.FromResult(0);
                            },
                            OnAuthenticationFailed = context =>
                            {
                                context.HttpContext.Items["AuthenticationFailed"] = true;
                                //Change the request url to something different and skip Wsfed. This new url will handle the request and let us know if this notification was invoked.
                                context.HttpContext.Request.Path = new PathString("/AuthenticationFailed");
                                context.SkipHandler();
                                return Task.FromResult(0);
                            },
                            OnRemoteSignOut = context =>
                            {
                                context.Response.Headers["EventHeader"] = "OnRemoteSignOut";
                                return Task.FromResult(0);
                            }
                        };
                    });
                });
            var server = new TestServer(builder);
            return server.CreateClient();
        }

        private void ConfigureApp(IApplicationBuilder app)
        {
            app.Map("/PreMapped-Challenge", mapped =>
            {
                mapped.UseAuthentication();
                mapped.Run(async context =>
                {
                    await context.ChallengeAsync(WsFederationDefaults.AuthenticationScheme);
                });
            });

            app.UseAuthentication();

            app.Map("/Logout", subApp =>
                {
                    subApp.Run(async context =>
                        {
                            if (context.User.Identity.IsAuthenticated)
                            {
                                var authProperties = new AuthenticationProperties() { RedirectUri = context.Request.GetEncodedUrl() };
                                await context.SignOutAsync(WsFederationDefaults.AuthenticationScheme, authProperties);
                                await context.Response.WriteAsync("Signing out...");
                            }
                            else
                            {
                                await context.Response.WriteAsync("SignedOut");
                            }
                        });
                });

            app.Map("/AuthenticationFailed", subApp =>
            {
                subApp.Run(async context =>
                {
                    await context.Response.WriteAsync("AuthenticationFailed");
                });
            });

            app.Map("/signout-wsfed", subApp =>
            {
                subApp.Run(async context =>
                {
                    await context.Response.WriteAsync("signout-wsfed");
                });
            });

            app.Map("/mapped-challenge", subApp =>
            {
                subApp.Run(async context =>
                {
                    await context.ChallengeAsync(WsFederationDefaults.AuthenticationScheme);
                });
            });

            app.Run(async context =>
            {
                var result = context.AuthenticateAsync();
                if (context.User == null || !context.User.Identity.IsAuthenticated)
                {
                    await context.ChallengeAsync(WsFederationDefaults.AuthenticationScheme);
                    await context.Response.WriteAsync("Unauthorized");
                }
                else
                {
                    var identity = context.User.Identities.Single();
                    if (identity.NameClaimType == "Name_Failed" && identity.RoleClaimType == "Role_Failed")
                    {
                        context.Response.StatusCode = 500;
                        await context.Response.WriteAsync("SignIn_Failed");
                    }
                    else if (!identity.HasClaim("Authenticated", "true") || !identity.HasClaim("ReturnEndpoint", "true") || !identity.HasClaim(identity.RoleClaimType, "Guest"))
                    {
                        await context.Response.WriteAsync("Provider not invoked");
                        return;
                    }
                    else
                    {
                        var token = await context.GetTokenAsync ("wsfed_token");
                        if (!string.IsNullOrWhiteSpace (token))
                        {
                            await context.Response.WriteAsync(token);
                        }
                        else 
                        {
                            await context.Response.WriteAsync(WsFederationDefaults.AuthenticationScheme);
                        }
                    }
                }
            });
        }

        private class WaadMetadataDocumentHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var metadata = File.ReadAllText(@"WsFederation/federationmetadata.xml");
                var newResponse = new HttpResponseMessage() { Content = new StringContent(metadata, Encoding.UTF8, "text/xml") };
                return Task.FromResult<HttpResponseMessage>(newResponse);
            }
        }
    }
}