using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RCONServerLib.Utils;

namespace RCONServerLib
{
    public class RemoteConClient
    {
        /// <summary>
        /// </summary>
        /// <param name="success">If the authentication request was successful or not</param>
        public delegate void AuthEventHandler(bool success);

        /// <summary>
        /// </summary>
        /// <param name="result">A string containing the answer of the server</param>
        public delegate void CommandResult(string result);

        /// <summary>
        /// </summary>
        /// <param name="type">The type of the change</param>
        public delegate void ConnectionEventHandler(ConnectionStateChange type);

        /// <summary>
        /// </summary>
        /// <param name="message">The message we want to log</param>
        public delegate void LogEventHandler(string message);

        public enum ConnectionStateChange
        {
            Connected,
            Disconnected,
            NoConnection,
            ConnectionTimeout,
            ConnectionLost
        }

        private const int ReadFixedAmountTimeoutMs = 10 * 1000;

        /// <summary>
        ///     The TCP Client
        /// </summary>
        private TcpClient _client;

        /// <summary>
        ///     A list containing all requested commands for event handling
        /// </summary>
        private readonly Dictionary<int, CommandResult> _requestedCommands;

        /// <summary>
        ///     A buffer containing the size of the packet
        /// </summary>
        private byte[] _sizeBuffer;
        
        /// <summary>
        ///     Underlaying NetworkStream
        /// </summary>
        private NetworkStream _ns;

        /// <summary>
        ///     Current packetId we're on
        /// </summary>
        private int _packetId;

        /// <summary>
        /// the <see cref="CancellationTokenSource"/> used to cancel the network stream reading loop
        /// </summary>
        private CancellationTokenSource _nsReadTaskCancellationTokenSource;
        
        /// <summary>
        ///     If the client is authenticated
        /// </summary>
        public bool Authenticated;

        public RemoteConClient()
        {
            _client = new TcpClient();

            _packetId = 0;
            _requestedCommands = new Dictionary<int, CommandResult>();

            UseUTF8 = false;
        }

        /// <summary>
        ///     Wether or not the TcpClient is still connected
        /// </summary>
        public bool Connected
        {
            get { return _client.Connected; }
        }

        /// <summary>
        ///     Whether to use UTF8 to encode the packet payload
        /// </summary>
        public bool UseUTF8 { get; set; }

        /// <summary>
        ///     An event handler when the result of the authentication is received
        /// </summary>
        public event AuthEventHandler OnAuthResult;

        /// <summary>
        ///     An event handler when the class wants to log something
        ///     Supressed when empty.
        /// </summary>
        public event LogEventHandler OnLog;

        /// <summary>
        ///     An event handler when the connection state changes
        ///     Ex. when disconnected or connection is lost
        /// </summary>
        public event ConnectionEventHandler OnConnectionStateChange;

        /// <summary>
        ///     Connects to the specified RCON Server
        /// </summary>
        /// <param name="hostname">The hostname of the RCON Server</param>
        /// <param name="port">The port to connect to</param>
        public void Connect(string hostname, int port)
        {
            Log($"Connecting to {hostname}:{port}");
            try
            {
                IAsyncResult ar = null;
                try
                {
                    ar = _client.BeginConnect(hostname, port, null, null);
                }
                catch (ObjectDisposedException)
                {
                    _client = new TcpClient();
                    try
                    {
                        ar = _client.BeginConnect(hostname, port, null, null);
                    }
                    catch (Exception e)
                    {
                        Log($"Unknown/Unexpected exception:\n{e}");
                    }
                }
                ar.AsyncWaitHandle.WaitOne(2000); // wait 2 seconds
                if (!ar.IsCompleted)
                {
                    OnConnectionStateChange?.Invoke(ConnectionStateChange.NoConnection);
                    _client.Client.Close();
                }
            }
            catch (SocketException)
            {
                if (OnConnectionStateChange != null)
                {
                    OnConnectionStateChange(ConnectionStateChange.ConnectionTimeout);
                    _client.Client.Close();
                }
                return;
            }

            if (!_client.Connected) return;
            _ns = _client.GetStream();

            _sizeBuffer = new byte[4];
            _nsReadTaskCancellationTokenSource = new CancellationTokenSource();
            _ = ListenToNetworkStreamAsync(_nsReadTaskCancellationTokenSource.Token);

            Log("Connected.");
            OnConnectionStateChange?.Invoke(ConnectionStateChange.Connected);
        }

        /// <summary>
        ///     Outputs a log to <see cref="OnLog" />
        /// </summary>
        /// <param name="message"></param>
        private void Log(string message)
        {
            OnLog?.Invoke(message);
        }

        /// <summary>
        ///     Disconnects the client from the server
        /// </summary>
        public void Disconnect()
        {
            if (_client.Connected)
            {
                _nsReadTaskCancellationTokenSource.Cancel();
                _client.Client.Disconnect(false);
                OnConnectionStateChange?.Invoke(ConnectionStateChange.Disconnected);
            }

            _client.Close();
        }

        /// <summary>
        ///     Sends the authentication to the server
        /// </summary>
        /// <param name="password">RCON Password</param>
        public void Authenticate(string password)
        {
            _packetId++;
            var packet = new RemoteConPacket(_packetId, RemoteConPacket.PacketType.Auth, password, UseUTF8);
            SendPacket(packet);
        }

        /// <summary>
        ///     Sends a RCON Command to the server
        /// </summary>
        /// <param name="command">The RCON command with parameters</param>
        /// <param name="resultFunc">A function that will be executed after the server has processed the request</param>
        /// <exception cref="NotAuthenticatedException">If we're not authenticated</exception>
        public void SendCommand(string command, CommandResult resultFunc)
        {
            if (!_client.Connected)
                return;

            if (!Authenticated)
                throw new NotAuthenticatedException();

            _packetId++;
            _requestedCommands.Add(_packetId, resultFunc);

            var packet = new RemoteConPacket(_packetId, RemoteConPacket.PacketType.ExecCommand, command, UseUTF8);
            SendPacket(packet);
        }

        /// <summary>
        ///     Sends the specified packet to the client
        /// </summary>
        /// <param name="packet">The packet to send</param>
        /// <exception cref="Exception">Not connected</exception>
        private void SendPacket(RemoteConPacket packet)
        {
            if (_client == null || !_client.Connected)
                throw new Exception("Not connected.");

            var packetBytes = packet.GetBytes();

            try
            {
                _ns.BeginWrite(packetBytes, 0, packetBytes.Length - 1, ar => { _ns.EndWrite(ar); }, null);
            }
            catch (ObjectDisposedException) { } // Do not write to NetworkStream when it's closed.
            catch (IOException) { } // Do not write to Socket when it's closed.
        }

        internal async Task ListenToNetworkStreamAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!await ReadFixedAmountAsync(_sizeBuffer, 0, 4, cancellationToken))
                        return;

                    int size = BinaryReaderExt.ToInt32LittleEndian(_sizeBuffer);
                    var packet = new byte[size];

                    if (!await ReadFixedAmountAsync(packet, 0, size, cancellationToken))
                        return;

                    ParsePacket(size, packet);
                }
                catch (TimeoutException) { }
                catch (ObjectDisposedException)
                {
                    OnConnectionStateChange?.Invoke(ConnectionStateChange.ConnectionLost);
                    Disconnect();
                    return;
                }
                catch (IOException)
                {
                    OnConnectionStateChange?.Invoke(ConnectionStateChange.ConnectionLost);
                    Disconnect();
                    return;
                }
                catch (Exception e)
                {
                    Log(e.ToString());
                    return;
                }
            }
        }

        /// <summary>
        /// <para>Continuously reads bytes from <see cref="_ns"/> until it read exactly <paramref name="count"/> amount of bytes</para>
        /// <para>If it started receiving bytes and doesn't receive enough bytes within
        /// <see cref="ReadFixedAmountTimeoutMs"/> a <see cref="TimeoutException"/> will be thrown</para>
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <param name="count"></param>
        /// <param name="cancellationToken"></param>
        /// <exception cref="TimeoutException"/>
        /// <returns><see langword="true"/> unless connection times out or cancellation is
        /// requested through the token both of which will also abort execution</returns>
        internal async Task<bool> ReadFixedAmountAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int actualCount = 0;
            int currentOffset = offset;
            DateTime? startTime = null;
            do
            {
                int readCount = await _ns.ReadAsync(buffer, currentOffset, count - actualCount, cancellationToken);
                if (cancellationToken.IsCancellationRequested || LostConnection())
                    return false;
                if (startTime.HasValue)
                {
                    if (DateTime.Now - startTime > TimeSpan.FromMilliseconds(ReadFixedAmountTimeoutMs))
                        throw new TimeoutException($"Expected to receive {count} bytes but only got {actualCount} after {ReadFixedAmountTimeoutMs}ms.");
                }
                else
                    startTime = DateTime.Now;
                actualCount += readCount;
                currentOffset += readCount;
            }
            while (actualCount < count);
            return true;
        }

        /// <summary>
        /// Did we loose connection?
        /// </summary>
        /// <returns></returns>
        internal bool LostConnection()
        {
            if (!_client.Connected)
            {
                OnConnectionStateChange?.Invoke(ConnectionStateChange.ConnectionLost);
                return true;
            }
            return false;
        }

        /// <summary>
        ///     Parses raw bytes to RemoteConPacket
        /// </summary>
        /// <param name="rawPacket"></param>
        internal void ParsePacket(int size, byte[] rawPacket)
        {
            try
            {
                var packet = new RemoteConPacket(rawPacket, UseUTF8, size);
                if (!Authenticated)
                {
                    // ExecCommand is AuthResponse too.
                    if (packet.Type == RemoteConPacket.PacketType.ExecCommand)
                    {
                        if (packet.Id == -1)
                        {
                            Log("Authentication failed.");
                            Authenticated = false;
                        }
                        else
                        {
                            Log("Authentication success.");
                            Authenticated = true;
                        }

                        OnAuthResult?.Invoke(Authenticated);
                    }

                    return;
                }

                if (_requestedCommands.ContainsKey(packet.Id) && packet.Type == RemoteConPacket.PacketType.ResponseValue)
                {
                    _requestedCommands[packet.Id](packet.Payload);
                }
                else
                {
                    Log("Got packet with invalid id " + packet.Id);
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
                Log(e.ToString());
            }
        }
    }
}
