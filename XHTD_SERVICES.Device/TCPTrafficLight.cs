using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.IO;

namespace XHTD_SERVICES.Device
{
    public class TCPTrafficLight
    {
        private const int BUFFER_SIZE = 1024;
        private const int PORT_NUMBER = 10000;

        private const string ONGREENOFFRED = "*[L1]ON[L2]OFF[!]";
        private const string OFFGREENONRED = "*[L1]OFF[L2]ON[!]";
        private const string OFFGREENOFFRED = "*[L1]OFF[L2]OFF[!]";

        private string IpAddress { get; set; }

        static ASCIIEncoding encoding = new ASCIIEncoding();

        public void Connect(string ipAddress)
        {
            IpAddress = ipAddress;
        }

        public bool TurnOnGreenOffRed()
        {
            try
            {
                TcpClient client = new TcpClient();

                // 1. connect
                client.Connect($"{this.IpAddress}", PORT_NUMBER);
                Stream stream = client.GetStream();

                // 2. send 1
                byte[] data1 = encoding.GetBytes($"{ONGREENOFFRED}");

                stream.Write(data1, 0, data1.Length);

                // 3. receive 1
                data1 = new byte[BUFFER_SIZE];
                stream.Read(data1, 0, BUFFER_SIZE);

                // 5. Close
                stream.Close();
                client.Close();

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                return false;
            }
        }

        public bool TurnOffGreenOnRed()
        {
            try
            {
                TcpClient client = new TcpClient();

                // 1. connect
                client.Connect($"{this.IpAddress}", PORT_NUMBER);
                Stream stream = client.GetStream();

                // 2. send 1
                byte[] data1 = encoding.GetBytes($"{OFFGREENONRED}");

                stream.Write(data1, 0, data1.Length);

                // 3. receive 1
                data1 = new byte[BUFFER_SIZE];
                stream.Read(data1, 0, BUFFER_SIZE);

                // 5. Close
                stream.Close();
                client.Close();

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                return false;
            }
        }

        public bool TurnOffGreenOffRed()
        {
            try
            {
                TcpClient client = new TcpClient();

                // 1. connect
                client.Connect($"{this.IpAddress}", PORT_NUMBER);
                Stream stream = client.GetStream();

                // 2. send 1
                byte[] data1 = encoding.GetBytes($"{OFFGREENOFFRED}");

                stream.Write(data1, 0, data1.Length);

                // 3. receive 1
                data1 = new byte[BUFFER_SIZE];
                stream.Read(data1, 0, BUFFER_SIZE);

                // 5. Close
                stream.Close();
                client.Close();

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                return false;
            }
        }
    }
}
