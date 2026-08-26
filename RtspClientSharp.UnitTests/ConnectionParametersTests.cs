using System;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RtspClientSharp.UnitTests
{
    [TestClass]
    public class ConnectionParametersTests
    {
        [TestMethod]
        public void ConnectionParameters_InitFromUriWithCredentials_HasValidCredentials()
        {
            var testUri = new Uri("rtsp://admin:123456@127.0.0.1");

            var conParams = new ConnectionParameters(testUri);

            Assert.AreEqual("admin", conParams.Credentials.UserName);
            Assert.AreEqual("123456", conParams.Credentials.Password);
        }

        [TestMethod]
        public void ConnectionParameters_InitFromUriAndSeparateCredentials_HasValidCredentials()
        {
            var testUri = new Uri("rtsp://test:pass@127.0.0.1");
            var credentilas = new NetworkCredential("admin", "123456");

            var conParams = new ConnectionParameters(testUri, credentilas);

            Assert.AreEqual("admin", conParams.Credentials.UserName);
            Assert.AreEqual("123456", conParams.Credentials.Password);
        }

        [TestMethod]
        public void ConnectionParameters_DefaultsToAutomaticMediaTransportDetection()
        {
            var conParams = new ConnectionParameters(new Uri("rtp://127.0.0.1:5004"));

            Assert.AreEqual(MediaTransportMode.Auto, conParams.TransportMode);
            Assert.IsFalse(conParams.UseTS);

            conParams.UseTS = true;
            Assert.AreEqual(MediaTransportMode.MpegTs, conParams.TransportMode);

            conParams.UseTS = false;
            Assert.AreEqual(MediaTransportMode.Rtp, conParams.TransportMode);
        }
    }
}
