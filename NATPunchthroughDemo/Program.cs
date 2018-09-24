using HiveMP.Lobby.Api;
using HiveMP.NATPunchthrough.Api;
using HiveMP.UserSession.Api;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace NATPunchthroughDemo
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            Console.WriteLine("Enter the details for the user account you want to use to sign into HiveMP.");

            var emailAddress = ReadLine.Read("Email address: ");
            var password = ReadLine.ReadPassword("Password: ");

            var apiKey = Environment.GetEnvironmentVariable("API_KEY");

            Console.WriteLine("Logging in...");
            var sessionClient = new UserSessionClient(apiKey);
            var session = await sessionClient.AuthenticatePUTAsync(new AuthenticatePUTRequest
            {
                Authentication = new AuthenticationRequest
                {
                    EmailAddress = emailAddress,
                    MarketingPreferenceOptIn = false,
                    Metered = true,
                    PasswordHash = HashPassword(password),
                    ProjectId = null,
                    PromptForProject = null,
                    RequestedRole = null,
                    Tokens = null,
                    TwoFactor = null
                }
            });

            if (session.AuthenticatedSession == null)
            {
                Console.Error.WriteLine("Unable to authenticate with HiveMP!");
                return;
            }

            Console.WriteLine("Finding a suitable game lobby...");
            var lobbyClient = new LobbyClient(session.AuthenticatedSession.ApiKey);
            var lobbies = lobbyClient.LobbiesPaginatedGETAsync(new LobbiesPaginatedGETRequest());
            LobbyInfo lobby;
            if (lobbies.Result.Results.Length == 0)
            {
                Console.WriteLine("Creating a game lobby because no lobby already exists.");
                lobby = await lobbyClient.LobbyPUTAsync(new LobbyPUTRequest
                {
                    MaxSessions = 0,
                    Name = "NAT Punchthrough Demo Lobby"
                });
            }
            else
            {
                lobby = lobbies.Result.Results.First();
                Console.WriteLine($"Using existing game lobby: {lobby.Id} \"{lobby.Name}\"");
            }

            Console.WriteLine("Joining the game lobby...");
            await lobbyClient.SessionPUTAsync(new HiveMP.Lobby.Api.SessionPUTRequest
            {
                LobbyId = lobby.Id,
                SessionId = session.AuthenticatedSession.Id,
            });

            var client = new PunchthroughClient(session.AuthenticatedSession.ApiKey);

            Console.WriteLine("Starting UDP client...");

            using (var listener = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
            {
                Console.WriteLine($"Now listening on port {listener.Client.LocalEndPoint}.");

                await Task.WhenAll(
                    ListenAndReceivePackets(listener),
                    SendPingPacketsToOtherClients(listener, lobbyClient, lobby, client, session.AuthenticatedSession),
                    PerformNatPunchthrough(listener, client, session.AuthenticatedSession)
                );
            }
        }

        private static async Task ListenAndReceivePackets(UdpClient listener)
        {
            while (listener.Client.IsBound)
            {
                var result = await listener.ReceiveAsync();
                if (result.Buffer.Length > 0)
                {
                    var message = Encoding.ASCII.GetString(result.Buffer);

                    Console.WriteLine("Recieved remote packet: " + message);
                }
            }
        }

        private static async Task SendPingPacketsToOtherClients(UdpClient listener, LobbyClient lobbyClient, LobbyInfo lobby, PunchthroughClient client, UserSessionWithSecrets authenticatedSession)
        {
            while (listener.Client.IsBound)
            {
                Console.WriteLine("Discovering other clients in game lobby...");
                var sessions = await lobbyClient.SessionsGETAsync(new SessionsGETRequest
                {
                    Id = lobby.Id
                });

                Console.WriteLine("Discovering endpoints for each session...");
                var sessionsToEndpoints = new Dictionary<string, IPEndPoint[]>();
                foreach (var session in sessions)
                {
                    var endpoints = await client.EndpointsGETAsync(new EndpointsGETRequest
                    {
                        Session = session.SessionId
                    });

                    Console.WriteLine($"Connected session {session.SessionId} has {endpoints.Length} endpoints.");
                    if (endpoints.Length != 0)
                    {
                        sessionsToEndpoints[session.SessionId] = endpoints.Select(x => new IPEndPoint(IPAddress.Parse(x.Host), x.Port.Value)).ToArray();
                    }
                }

                Console.WriteLine("Sending PING packets to all discovered endpoints...");
                foreach (var kv in sessionsToEndpoints)
                {
                    foreach (var endpoint in kv.Value)
                    {
                        Console.WriteLine("Sending PING to " + endpoint);
                        var bytes = Encoding.ASCII.GetBytes($"PING from {authenticatedSession.Id}");
                        await listener.SendAsync(bytes, bytes.Length, endpoint);
                    }
                }

                // Sleep a little since we don't want to spam other clients with ping messages.
                await Task.Delay(5000);
            }
        }

        private static async Task PerformNatPunchthrough(UdpClient listener, PunchthroughClient client, UserSessionWithSecrets session)
        {
            Console.WriteLine("Getting NAT punchthrough message...");
            var message = await client.PunchthroughPUTAsync(new PunchthroughPUTRequest
            {
                Session = session.Id
            });

            Console.WriteLine($"Will send NAT punchthrough message to {message.Host}:{message.Port.Value}...");

            var connected = false;
            while (!connected)
            {
                Console.WriteLine("Sending UDP packet...");

                // Send a UDP packet to the NAT punchthrough server.
                await listener.SendAsync(
                    message.Message,
                    message.Message.Length,
                    message.Host,
                    message.Port.Value);

                Console.WriteLine("Waiting...");

                // Wait a little bit.
                await Task.Delay(1000);

                Console.WriteLine("Checking if NAT punchthrough is complete...");

                // Check if the NAT punchthrough has worked.
                var result = await client.PunchthroughGETAsync(new PunchthroughGETRequest
                {
                    Session = session.Id
                });

                if (result)
                {
                    // Punchthrough complete, return.
                    Console.WriteLine("NAT punchthrough completed successfully!");
                    var endpoints = await client.EndpointsGETAsync(new EndpointsGETRequest
                    {
                        Session = session.Id
                    });
                    Console.WriteLine("Available at the following endpoints:");
                    foreach (var endpoint in endpoints)
                    {
                        Console.WriteLine($" - {endpoint.Host}:{endpoint.Port.Value}");
                    }

                    connected = true;
                    return;
                }
            }
        }

        private static string HashPassword(string password)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("HiveMPv1" + password))).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
