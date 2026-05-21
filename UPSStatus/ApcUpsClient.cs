using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UPSStatus
{
    public class ApcUpsStatus
    {
        public string Status { get; set; } = "";
        public decimal BatteryCharge { get; set; }
        public decimal TimeLeftMinutes { get; set; }
        public decimal LoadPercent { get; set; }
        public decimal LineVoltage { get; set; }
        public Dictionary<string, string> RawValues { get; set; } = new();
    }

    public static class ApcUpsClient
    {
        private const int RetryDelaySeconds = 10;

        public static Task<ApcUpsStatus> GetStatusAsync(string host, int port,
            Action<int>? onRetry = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(async () =>
            {
                int attempt = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        return GetStatus(host, port);
                    }
                    catch (SocketException) when (!cancellationToken.IsCancellationRequested)
                    {
                        attempt++;
                        for (int i = RetryDelaySeconds; i > 0; i--)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            onRetry?.Invoke(i);
                            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                        }
                    }
                }
            }, cancellationToken);
        }

        private static ApcUpsStatus GetStatus(string host, int port)
        {
            using var client = new TcpClient();
            client.Connect(host, port);

            using NetworkStream stream = client.GetStream();

            SendCommand(stream, "status");

            string response = ReadResponse(stream);

            return ParseStatus(response);
        }

        private static void SendCommand(NetworkStream stream, string command)
        {
            byte[] commandBytes = Encoding.ASCII.GetBytes(command);

            // APCUPSD expects a 2-byte big-endian length prefix
            byte[] lengthBytes =
            {
                (byte)(commandBytes.Length >> 8),
                (byte)(commandBytes.Length & 0xff)
            };

            stream.Write(lengthBytes, 0, 2);
            stream.Write(commandBytes, 0, commandBytes.Length);
        }

        private static string ReadResponse(NetworkStream stream)
        {
            var sb = new StringBuilder();

            while (true)
            {
                int high = stream.ReadByte();
                int low = stream.ReadByte();

                if (high < 0 || low < 0)
                    break;

                int length = (high << 8) + low;

                // Length 0 means end of response
                if (length == 0)
                    break;

                byte[] buffer = new byte[length];
                int read = 0;

                while (read < length)
                {
                    int bytesRead = stream.Read(buffer, read, length - read);

                    if (bytesRead <= 0)
                        throw new Exception("Connection closed while reading APCUPSD response.");

                    read += bytesRead;
                }

                sb.Append(Encoding.ASCII.GetString(buffer));
            }

            return sb.ToString();
        }

        private static ApcUpsStatus ParseStatus(string response)
        {
            var result = new ApcUpsStatus();

            string[] lines = response.Split(
                new[] { "\r\n", "\n" },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (string line in lines)
            {
                int colonIndex = line.IndexOf(':');

                if (colonIndex < 0)
                    continue;

                string key = line[..colonIndex].Trim();
                string value = line[(colonIndex + 1)..].Trim();

                result.RawValues[key] = value;
            }

            result.Status = GetString(result.RawValues, "STATUS");
            result.BatteryCharge = GetDecimal(result.RawValues, "BCHARGE");
            result.TimeLeftMinutes = GetDecimal(result.RawValues, "TIMELEFT");
            result.LoadPercent = GetDecimal(result.RawValues, "LOADPCT");
            result.LineVoltage = GetDecimal(result.RawValues, "LINEV");

            return result;
        }

        private static string GetString(Dictionary<string, string> values, string key)
        {
            return values.TryGetValue(key, out string? value) ? value : "";
        }

        private static decimal GetDecimal(Dictionary<string, string> values, string key)
        {
            if (!values.TryGetValue(key, out string? value))
                return 0;

            // APCUPSD values look like:
            // "11.0 Percent"
            // "5.5 Minutes"
            // "120.0 Volts"
            string numberPart = value.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

            return decimal.TryParse(numberPart, out decimal result) ? result : 0;
        }
    }
}
