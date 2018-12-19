using System;
using Neo.Core.Networking;

namespace Neo.Server
{
    internal class NeoServer : BaseServer
    {
        public override void OnConnect(Client client) { }

        public override void OnDisconnect(string clientId, ushort code, string reason, bool wasClean) { }

        public override void OnError(string clientId, Exception ex, string message) { }

        public override void OnMessage(string clientId, string message) {
            Console.WriteLine(clientId + ": " + message);
        }
    }
}
