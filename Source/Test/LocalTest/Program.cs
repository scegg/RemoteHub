﻿using SecretNest.RemoteHub;
using System;
using System.Collections.Generic;

namespace LocalTest
{
    class Program
    {
        const string connectionString = "localhost";
        const string mainChannel = "Main";
        const string hostKeyPrefix = "TestClient_";
        const string privateChannelPrefix = "Private_";
        static Dictionary<Guid, string> clientNames = new Dictionary<Guid, string>();

        static void Main(string[] args)
        {
            Guid client1Id = Guid.NewGuid();
            Guid client2Id = Guid.NewGuid();
            Guid client3Id = Guid.NewGuid();
            RemoteHubOfString client1 = new RemoteHubOfString(client1Id, connectionString, mainChannel, hostKeyPrefix, privateChannelPrefix, 0);
            RemoteHubOfString client2 = new RemoteHubOfString(client2Id, connectionString, mainChannel, hostKeyPrefix, privateChannelPrefix, 0);
            RemoteHubOfString client3 = new RemoteHubOfString(client3Id, connectionString, mainChannel, hostKeyPrefix, privateChannelPrefix, 0);
            client1.OnMessageReceivedCallback = Received;
            client2.OnMessageReceivedCallback = Received;
            client3.OnMessageReceivedCallback = Received;

            //Console.WriteLine(string.Format("ClientId: {0} {1} {2}", client1Id, client2Id, client3Id));
            clientNames.Add(client1Id, "Client1");
            clientNames.Add(client2Id, "Client2");
            clientNames.Add(client3Id, "Client3");

            client1.Start();
            client2.Start();
            client3.Start();

            bool shouldContinue = true;
            while (shouldContinue)
            {
                Console.WriteLine(@"Press:
1: Send from client 1 to client 1
2: Send from client 1 to client 2
3: Send from client 1 to client 3
4: Send from client 2 to client 1
5: Send from client 2 to client 2
6: Send from client 2 to client 3
7: Send from client 3 to client 1
8: Send from client 3 to client 2
9: Send from client 3 to client 3
Other: Shutdown.");

                var key = Console.ReadKey(true);
                switch (key.Key)
                {
                    case ConsoleKey.D1:
                        client1.SendMessage(client1Id, "From 1 to 1");
                        break;
                    case ConsoleKey.D2:
                        client1.SendMessage(client2Id, "From 1 to 2");
                        break;
                    case ConsoleKey.D3:
                        client1.SendMessage(client3Id, "From 1 to 3");
                        break;
                    case ConsoleKey.D4:
                        client1.SendMessage(client1Id, "From 2 to 1");
                        break;
                    case ConsoleKey.D5:
                        client1.SendMessage(client2Id, "From 2 to 2");
                        break;
                    case ConsoleKey.D6:
                        client1.SendMessage(client3Id, "From 2 to 3");
                        break;
                    case ConsoleKey.D7:
                        client1.SendMessage(client1Id, "From 3 to 1");
                        break;
                    case ConsoleKey.D8:
                        client1.SendMessage(client2Id, "From 3 to 2");
                        break;
                    case ConsoleKey.D9:
                        client1.SendMessage(client3Id, "From 3 to 3");
                        break;
                    default:
                        shouldContinue = false;
                        break;
                }
            }

            client1.Shutdown();
            client2.Shutdown();
            client3.Shutdown();

            Console.WriteLine("Done. Press any key to quit...");
            Console.ReadKey(true);
        }

        static void Received(Guid clientId, string text)
        {
            Console.WriteLine(string.Format("Received: {0}: {1}", clientNames[clientId], text));
        }
    }
}
