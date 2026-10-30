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

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using SharpOnvifClient;

namespace SharpOnvif.Tests
{
    /// <summary>
    /// Probing several interfaces costs one window, not one per interface.
    /// <para>
    /// A scan sends a Probe on every interface and waits the window out for answers. The probes
    /// were queued behind a static semaphore, so a machine with four addresses - wired, wireless,
    /// a virtual switch, a VPN - took four windows over a scan that should take one, and every
    /// caller in the process queued behind every other.
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class TestDiscoveryConcurrency
    {
        private const int WindowMilliseconds = 1500;

        [TestMethod]
        [Timeout(30000)]
        public async Task ProbesTwoInterfacesInOneWindow()
        {
            // Loopback: a Probe from it reaches nothing, which does not matter - the window is
            // waited out either way, and the window is what is measured. Two probes on the one
            // address stand in for two interfaces; each binds an ephemeral port, so they do not
            // collide.
            var discovery = new OnvifDiscoveryClient();
            var stopwatch = Stopwatch.StartNew();

            await Task.WhenAll(
                discovery.DiscoverAsync("127.0.0.1", multicastTimeout: WindowMilliseconds),
                discovery.DiscoverAsync("127.0.0.1", multicastTimeout: WindowMilliseconds));

            stopwatch.Stop();

            Assert.IsTrue(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(WindowMilliseconds - 100),
                $"a probe answered after {stopwatch.Elapsed}, before its window had closed");
            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromMilliseconds(2 * WindowMilliseconds - 300),
                $"two probes took {stopwatch.Elapsed}: a window each in turn, rather than one window together");
        }
    }
}
