using System;
using Neo.Core.Communication;
using Neo.Core.Networking;

namespace Neo.Server
{
    internal class NeoServer : BaseServer
    {
        //public override void OnConnect(string clientId) { }
        public override void OnConnect(Client client) {
            Clients.Add(client);
        }

        public override void OnDisconnect(string clientId, ushort code, string reason, bool wasClean) { }

        public override void OnError(string clientId, Exception ex, string message) { }

        public override void OnPackage(string clientId, Package package) {
            Console.WriteLine(clientId + ": " + package.Content);
        }
    }
}
