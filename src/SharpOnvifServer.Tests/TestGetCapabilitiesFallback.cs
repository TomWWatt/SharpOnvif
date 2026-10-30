// SharpOnvif
// Copyright (C) 2026 Lukas Volf
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpOnvifClient;
using SharpOnvifCommon.Xml;
using SharpOnvifServer.Dispatch;

namespace SharpOnvif.Tests
{
    /// <summary>
    /// A device on older firmware, met over the wire: it faults GetServices - as this server does
    /// for an operation the implementation left out - and answers GetCapabilities. The client
    /// still has to find the Media service.
    /// </summary>
    [TestClass]
    public sealed class TestGetCapabilitiesFallback
    {
        private const string EndpointPath = "/onvif/device_service";

        private static WebApplication _app;
        private static string _endpoint;

        /// <summary>
        /// GetServices is deliberately not overridden: the base answers it with the
        /// ActionNotSupported fault, which is what a device that predates it sends.
        /// </summary>
        private sealed class DeviceImpl : SharpOnvifServer.DeviceMgmt.DeviceBase
        {
            public override SharpOnvifServer.DeviceMgmt.GetCapabilitiesResponse GetCapabilities(
                SharpOnvifCommon.Onvif.CapabilityCategory[] Category)
            {
                return new SharpOnvifServer.DeviceMgmt.GetCapabilitiesResponse
                {
                    Capabilities = new SharpOnvifCommon.Onvif.Capabilities
                    {
                        Device = new SharpOnvifCommon.Onvif.DeviceCapabilities { XAddr = _endpoint },
                        Media = new SharpOnvifCommon.Onvif.MediaCapabilities { XAddr = _endpoint },
                    },
                };
            }
        }

        private sealed class MediaImpl : SharpOnvifServer.Media.MediaBase
        {
            public override SharpOnvifServer.Media.GetProfilesResponse GetProfiles(
                SharpOnvifServer.Media.GetProfilesRequest request)
            {
                return new SharpOnvifServer.Media.GetProfilesResponse
                {
                    Profiles = new[] { new SharpOnvifCommon.Onvif.Profile { token = "p0", Name = "MainStream" } },
                };
            }
        }

        [ClassInitialize]
        public static async Task StartServer(TestContext context)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            builder.Services.AddSingleton<DeviceImpl>();
            builder.Services.AddSingleton<MediaImpl>();

            _app = builder.Build();
            _app.MapOnvifService<DeviceImpl>(EndpointPath);
            _app.MapOnvifService<MediaImpl>(EndpointPath);

            await _app.StartAsync();

            string address = _app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>().Addresses.First();
            _endpoint = address.TrimEnd('/') + EndpointPath;
        }

        [ClassCleanup]
        public static async Task StopServer()
        {
            if (_app != null) await _app.StopAsync();
        }

        [TestMethod]
        public async Task ReachesTheMediaServiceOfADeviceThatFaultsGetServices()
        {
            using (var client = new SimpleOnvifClient(_endpoint))
            {
                await Assert.ThrowsExactlyAsync<SoapFaultException>(() => client.GetServicesAsync(),
                    "the device has to fault GetServices for the rest of this to mean anything");

                var profiles = await client.GetProfilesAsync();

                Assert.AreEqual(1, profiles.Profiles.Length);
                Assert.AreEqual("MainStream", profiles.Profiles[0].Name);
            }
        }
    }
}
