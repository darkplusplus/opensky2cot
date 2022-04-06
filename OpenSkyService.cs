using dpp.cot;
using dpp.takclient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace opensky2cot
{
    public class OpenSkyService : BackgroundService, IDisposable
    {
        private readonly TakClient _takClient;
        private readonly OpenSkyNet.OpenSkyApi _openSkyClient;
        private readonly int _interval;
        private readonly IConfiguration _configuration;
        private bool _disposed;

        public OpenSkyService(IConfiguration configuration)
        {
            _configuration = configuration;

            string address = configuration.GetValue("cot:address", "127.0.0.1");
            int port = configuration.GetValue("cot:port", 8087);
            int? backoff = configuration.GetValue("cot:backoff", 300000);

            _interval = configuration.GetValue("opensky:interval", 3000);

            var username = configuration["opensky:username"];
            var password = configuration["opensky:password"];
            if (username is not null && password is not null)
            {
                _openSkyClient = new OpenSkyNet.OpenSkyApi(username, password);
            }
            else
            {
                _openSkyClient = new OpenSkyNet.OpenSkyApi();
            }

            _takClient = new TakClient(
                Dns.GetHostAddresses(address).First().ToString(),
                port, 
                backoff
            );
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _takClient.Connect();

            while (!stoppingToken.IsCancellationRequested)
            {
                Log.Information("Worker running at {time}", DateTime.Now);

                if (_takClient.IsConnected)
                {
                    var response = await _openSkyClient.GetStatesAsync();
                    foreach (var state in response.States)
                    {
                        try
                        {
                            var time = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds((int)state.TimePosition);

                            if (state.Latitude is null || state.Longitude is null)
                                continue;

                            var callsign = (state.CallSign ?? state.Icao24).Trim();

                            var msg = new Message()
                            {
                                Event = new Event()
                                {
                                    Version = "2.0",
                                    Uid = $"opensky-{state.Icao24}",
                                    How = "m-p",
                                    Type = LookupType(state.Icao24),
                                    Time = time,
                                    Start = time,
                                    Stale = time.AddSeconds(_interval),
                                    Point = new Point()
                                    {
                                        Lat = (double)state.Latitude,
                                        Lon = (double)state.Longitude,
                                        Hae = state.Altitude ?? 9999999.0,
                                        //Ce = 20, // TODO: should this projected from velocity?
                                        //Le = 20,
                                    },
                                    Detail = new Detail()
                                    {
                                        Contact = new Contact()
                                        {
                                            Callsign = callsign,
                                        },
                                        // TODO: track? precison location?
                                    }
                                }
                            };

                            _takClient.Send(msg.ToXmlString());
                            Log.Information("handling state vector for {icao}", state.Icao24);
                        }
                        catch (Exception e)
                        {
                            Log.Information("Failed to parse state vector", e);
                        }
                    }
                }
                else
                {
                    Log.Information("pending connection to {}, defering opensky data", _takClient.Endpoint.ToString());
                }

                await Task.Delay(_interval, stoppingToken);
            }
        }

        private string LookupType(string icao24)
        {
            // TODO: do lookup of icao24 to determine types.
            return @"a-n-A-C-F";
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _takClient.Dispose();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}