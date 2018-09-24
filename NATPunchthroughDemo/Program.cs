using HiveMP.NATPunchthrough.Api;
using HiveMP.UserSession.Api;
using System;
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

            var client = new PunchthroughClient(session.AuthenticatedSession.ApiKey);

            Console.WriteLine("Starting UDP client...");

            using (var listener = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
            {
                Console.WriteLine($"Now listening on port {listener.Client.LocalEndPoint}.");

                await Task.WhenAny(
                    ListenAndReceivePackets(listener),
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
                    Console.WriteLine("Recieved remote packet.");
                }
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
