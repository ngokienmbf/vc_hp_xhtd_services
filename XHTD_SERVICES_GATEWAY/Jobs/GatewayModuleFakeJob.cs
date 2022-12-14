using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Quartz;
using XHTD_SERVICES.Data.Repositories;
using XHTD_SERVICES_GATEWAY.Models.Response;
using XHTD_SERVICES.Data.Models.Response;
using System.Runtime.InteropServices;
using XHTD_SERVICES.Device.PLCM221;
using XHTD_SERVICES.Device;
using XHTD_SERVICES.Data.Entities;
using Newtonsoft.Json;
using XHTD_SERVICES.Helper;
using Microsoft.AspNetCore.SignalR.Client;
using System.Collections.Specialized;
using System.Configuration;

namespace XHTD_SERVICES_GATEWAY.Jobs
{
    public class GatewayModuleFakeJob : IJob
    {
        protected readonly StoreOrderOperatingRepository _storeOrderOperatingRepository;

        protected readonly RfidRepository _rfidRepository;

        protected readonly CategoriesDevicesRepository _categoriesDevicesRepository;

        protected readonly CategoriesDevicesLogRepository _categoriesDevicesLogRepository;

        protected readonly SystemParameterRepository _systemParameterRepository;

        protected readonly Barrier _barrier;

        protected readonly TCPTrafficLight _trafficLight;

        protected readonly Notification _notification;

        protected readonly GatewayLogger _gatewayLogger;

        private IntPtr h21 = IntPtr.Zero;

        private static bool DeviceConnected = false;

        private List<CardNoLog> tmpCardNoLst_In = new List<CardNoLog>();

        private List<CardNoLog> tmpCardNoLst_Out = new List<CardNoLog>();

        private List<CardNoLog> tmpInvalidCardNoLst = new List<CardNoLog>();

        private tblCategoriesDevice c3400, rfidRa1, rfidRa2, rfidVao1, rfidVao2, m221, barrierVao, barrierRa, trafficLightVao, trafficLightRa;

        protected const string CBV_ACTIVE = "CBV_ACTIVE";

        private static bool isActiveService = true;

        private string HubURL;

        private string RFIDValue;

        private bool IsJustReceivedRFIDData = false;

        private HubConnection Connection { get; set; }

        [DllImport(@"C:\Windows\System32\plcommpro.dll", EntryPoint = "Connect")]
        public static extern IntPtr Connect(string Parameters);

        [DllImport(@"C:\Windows\System32\plcommpro.dll", EntryPoint = "PullLastError")]
        public static extern int PullLastError();

        [DllImport(@"C:\Windows\System32\plcommpro.dll", EntryPoint = "GetRTLog")]
        public static extern int GetRTLog(IntPtr h, ref byte buffer, int buffersize);

        public GatewayModuleFakeJob(
            StoreOrderOperatingRepository storeOrderOperatingRepository, 
            RfidRepository rfidRepository,
            CategoriesDevicesRepository categoriesDevicesRepository,
            CategoriesDevicesLogRepository categoriesDevicesLogRepository,
            SystemParameterRepository systemParameterRepository,
            Barrier barrier,
            TCPTrafficLight trafficLight,
            Notification notification,
            GatewayLogger gatewayLogger
            )
        {
            _storeOrderOperatingRepository = storeOrderOperatingRepository;
            _rfidRepository = rfidRepository;
            _categoriesDevicesRepository = categoriesDevicesRepository;
            _categoriesDevicesLogRepository = categoriesDevicesLogRepository;
            _systemParameterRepository = systemParameterRepository;
            _barrier = barrier;
            _trafficLight = trafficLight;
            _notification = notification;
            _gatewayLogger = gatewayLogger;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            await Task.Run(async () =>
            {
                // Get System Parameters
                await LoadSystemParameters();

                if (!isActiveService)
                {
                    _gatewayLogger.LogInfo("Service cong bao ve dang TAT.");
                    return;
                }

                _gatewayLogger.LogInfo("Start gateway fake service");
                _gatewayLogger.LogInfo("----------------------------");

                HandleHubConnection();

                // Get devices info
                await LoadDevicesInfo();

                AuthenticateGatewayModule();
            });                                                                                                                     
        }

        public async Task LoadSystemParameters()
        {
            var parameters = await _systemParameterRepository.GetSystemParameters();

            var activeParameter = parameters.FirstOrDefault(x => x.Code == CBV_ACTIVE);

            if (activeParameter == null || activeParameter.Value == "0")
            {
                isActiveService = false;
            }
        }

        public async Task LoadDevicesInfo()
        {
            var devices = await _categoriesDevicesRepository.GetDevices("CBV");

            c3400 = devices.FirstOrDefault(x => x.Code == "CBV.C3-400");
            rfidRa1 = devices.FirstOrDefault(x => x.Code == "CBV.C3-400.RFID-OUT-1");
            rfidRa2 = devices.FirstOrDefault(x => x.Code == "CBV.C3-400.RFID-OUT-1");
            rfidVao1 = devices.FirstOrDefault(x => x.Code == "CBV.C3-400.RFID-IN-1");
            rfidVao2 = devices.FirstOrDefault(x => x.Code == "CBV.C3-400.RFID-IN-2");

            m221 = devices.FirstOrDefault(x => x.Code == "CBV.M221");
            barrierVao = devices.FirstOrDefault(x => x.Code == "CBV.M221.BRE-IN");
            barrierRa = devices.FirstOrDefault(x => x.Code == "CBV.M221.BRE-OUT");
            trafficLightVao = devices.FirstOrDefault(x => x.Code == "CBV.DGT-IN");
            trafficLightRa = devices.FirstOrDefault(x => x.Code == "CBV.DGT-OUT");
        }

        public async void HandleHubConnection()
        {
            var apiUrl = ConfigurationManager.GetSection("API_DMS/Url") as NameValueCollection;
            HubURL = apiUrl["ScaleHub"];

            var reconnectSeconds = new List<TimeSpan> { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(5) };

            var i = 5;
            while (i <= 7200)
            {
                reconnectSeconds.Add(TimeSpan.FromSeconds(i));
                i++;
            }

            Connection = new HubConnectionBuilder()
                .WithUrl($"{HubURL}")
                //.WithAutomaticReconnect()
                .Build();

            Connection.On<HUBResponse>("SendMsgToUser", fakeHubResponse =>
            {
                if (fakeHubResponse != null && fakeHubResponse.Data != null && fakeHubResponse.Data.Vehicle != "")
                {
                    RFIDValue = fakeHubResponse.Data.Vehicle;
                    IsJustReceivedRFIDData = true;
                }
            });

            try
            {
                await Connection.StartAsync();
                Console.WriteLine("Connected!");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Disconnect!");
            }

            Connection.Reconnecting += connectionId =>
            {
                Console.WriteLine("Reconnecting....");
                return Task.CompletedTask;
            };

            Connection.Reconnected += connectionId =>
            {
                Console.WriteLine("Connected!");
                return Task.CompletedTask;
            };

            Connection.Closed += async (error) =>
            {
                Console.WriteLine("Closed!");

                await Task.Delay(new Random().Next(0, 5) * 1000);
                await Connection.StartAsync();
            };
        }

        public void AuthenticateGatewayModule()
        {
            /*
             * 1. Xác định xe vào hay ra cổng theo gia tri door từ C3-400
             * 2. Loại bỏ các cardNoCurrent đã, đang xử lý (đã check trước đó)
             * 3. Kiểm tra cardNoCurrent có hợp lệ hay không
             * 4. Kiểm tra cardNoCurrent có đang chứa đơn hàng hợp lệ không
             * 5. Cập nhật đơn hàng: Step
             * 6. Bật đèn xanh giao thông
             * 7. Mở barrier
             * 8. Ghi log thiết bị
             * 9. Bắn tín hiệu thông báo
             */

            // 1. Connect Device
            while (!DeviceConnected)
            {
                ConnectGatewayModule();
            }

            // 2. Đọc dữ liệu từ thiết bị
            ReadDataFromC3400();

        }

        public bool ConnectGatewayModule()
        {
            _gatewayLogger.LogInfo("Connected to C3-400");

            DeviceConnected = true;
                    
            return DeviceConnected;
        }

        public async void ReadDataFromC3400()
        {
            _gatewayLogger.LogInfo("Read data from C3-400");

            if (DeviceConnected)
            {
                while (DeviceConnected)
                {
                    string str;
                    string[] tmp = null;

                    if (IsJustReceivedRFIDData)
                    {
                        IsJustReceivedRFIDData = false;

                        str = RFIDValue != null ? RFIDValue : "";
                        tmp = str.Split(',');

                        // Trường hợp bắt được tag RFID
                        if (tmp != null && tmp.Count() > 3 && tmp[2] != "0" && tmp[2] != "") {

                            var cardNoCurrent = tmp[2]?.ToString();
                            var doorCurrent = tmp[3]?.ToString();

                            _gatewayLogger.LogInfo("----------------------------");
                            _gatewayLogger.LogInfo($"Tag: {cardNoCurrent}, door: {doorCurrent}");
                            _gatewayLogger.LogInfo("-----");

                            // 1.Xác định xe cân vào / ra
                            var isLuongVao = doorCurrent == rfidVao1.PortNumberDeviceIn.ToString()
                                            || doorCurrent == rfidVao2.PortNumberDeviceIn.ToString();

                            var isLuongRa = doorCurrent == rfidRa1.PortNumberDeviceIn.ToString()
                                            || doorCurrent == rfidRa2.PortNumberDeviceIn.ToString();

                            var direction = 0;

                            if (isLuongVao)
                            {
                                direction = 1;
                                _gatewayLogger.LogInfo($"1. Xe can vao");
                            }
                            else
                            {
                                direction = 2;
                                _gatewayLogger.LogInfo($"1. Xe can ra");
                            }

                            // 2. Loại bỏ các tag đã check trước đó
                            if (tmpInvalidCardNoLst.Count > 5) tmpInvalidCardNoLst.RemoveRange(0, 3);

                            if (tmpInvalidCardNoLst.Exists(x => x.CardNo.Equals(cardNoCurrent) && x.DateTime > DateTime.Now.AddMinutes(-2)))
                            {
                                _gatewayLogger.LogInfo($@"2. Tag da duoc check truoc do => Ket thuc.");

                                continue;
                            }

                            if (isLuongVao) {
                                if (tmpCardNoLst_In.Count > 5) tmpCardNoLst_In.RemoveRange(0, 3);

                                if (tmpCardNoLst_In.Exists(x => x.CardNo.Equals(cardNoCurrent) && x.DateTime > DateTime.Now.AddMinutes(-1)))
                                {
                                    _gatewayLogger.LogInfo($@"2. Tag da duoc check truoc do => Ket thuc.");

                                    continue;
                                }
                            }
                            else if (isLuongRa)
                            {
                                if (tmpCardNoLst_Out.Count > 5) tmpCardNoLst_Out.RemoveRange(0, 3);

                                if (tmpCardNoLst_Out.Exists(x => x.CardNo.Equals(cardNoCurrent) && x.DateTime > DateTime.Now.AddMinutes(-1)))
                                {
                                    _gatewayLogger.LogInfo($@"2. Tag da duoc check truoc do => Ket thuc.");

                                    continue;
                                }
                            }

                            _gatewayLogger.LogInfo($"2. Kiem tra tag da check truoc do");

                            // 3. Kiểm tra cardNoCurrent có hợp lệ hay không
                            bool isValid = _rfidRepository.CheckValidCode(cardNoCurrent);

                            if (isValid)
                            {
                                _gatewayLogger.LogInfo($"3. Tag hop le");
                            }
                            else
                            {
                                _gatewayLogger.LogInfo($"3. Tag KHONG hop le => Ket thuc.");

                                _notification.SendNotification(
                                    "CBV",
                                    null,
                                    0,
                                    "RFID không thuộc hệ thống",
                                    direction,
                                    null,
                                    null,
                                    Convert.ToInt32(cardNoCurrent),
                                    null,
                                    null,
                                    null
                                );

                                // Cần add các thẻ invalid vào 1 mảng để tránh phải check lại
                                // Chỉ check lại các invalid tag sau 1 khoảng thời gian: 3 phút
                                var newCardNoLog = new CardNoLog { CardNo = cardNoCurrent, DateTime = DateTime.Now };
                                tmpInvalidCardNoLst.Add(newCardNoLog);

                                continue;
                            }

                            // 4. Kiểm tra cardNoCurrent có đang chứa đơn hàng hợp lệ không
                            List <tblStoreOrderOperating> currentOrders = null;
                            if (isLuongVao)
                            {
                                currentOrders = await _storeOrderOperatingRepository.GetCurrentOrdersEntraceGatewayByCardNoReceiving(cardNoCurrent);
                            }
                            else if (isLuongRa){
                                currentOrders = await _storeOrderOperatingRepository.GetCurrentOrdersExitGatewayByCardNoReceiving(cardNoCurrent);
                            }

                            if (currentOrders == null || currentOrders.Count == 0) {
                                
                                _gatewayLogger.LogInfo($"4. Tag KHONG co don hang hop le => Ket thuc.");

                                _notification.SendNotification(
                                    "CBV",
                                    null,
                                    0,
                                    "RFID không có đơn hàng hợp lệ",
                                    direction,
                                    null,
                                    null,
                                    Convert.ToInt32(cardNoCurrent),
                                    null,
                                    null,
                                    null
                                );

                                // Cần add các thẻ invalid vào 1 mảng để tránh phải check lại
                                // Chỉ check lại các invalid tag sau 1 khoảng thời gian: 3 phút
                                var newCardNoLog = new CardNoLog { CardNo = cardNoCurrent, DateTime = DateTime.Now };
                                tmpInvalidCardNoLst.Add(newCardNoLog);

                                continue;
                            }

                            var currentOrder = currentOrders.FirstOrDefault();
                            var deliveryCodes = String.Join(";", currentOrders.Select(x => x.DeliveryCode).ToArray());

                            _gatewayLogger.LogInfo($"4. Tag co cac don hang hop le DeliveryCode = {deliveryCodes}");

                            _notification.SendNotification(
                                "CBV",
                                null,
                                1,
                                "RFID có đơn hàng hợp lệ",
                                direction,
                                null,
                                null,
                                Convert.ToInt32(cardNoCurrent),
                                null,
                                null,
                                null
                            );

                            // 5. Cập nhật đơn hàng
                            var isUpdatedOrder = false;

                            if (isLuongVao)
                            {
                                isUpdatedOrder = await _storeOrderOperatingRepository.UpdateOrderEntraceGateway(cardNoCurrent);
                            }
                            else if (isLuongRa)
                            {
                                isUpdatedOrder = await _storeOrderOperatingRepository.UpdateOrderExitGateway(cardNoCurrent);
                            }

                            if (isUpdatedOrder)
                            {
                                _gatewayLogger.LogInfo($"5. Update don hang thanh cong.");

                                var newCardNoLog = new CardNoLog { CardNo = cardNoCurrent, DateTime = DateTime.Now };

                                /*
                                 * 6. Bật đèn xanh giao thông, 
                                 * 7. Mở barrier
                                 * 8. Ghi log thiết bị
                                 * 9. Bắn tín hiệu thông báo
                                 */
                                bool isSuccessTurnOnGreenTrafficLight = false;
                                bool isSuccessOpenBarrier = false;

                                if (isLuongVao)
                                {
                                    tmpCardNoLst_In.Add(newCardNoLog);

                                    isSuccessOpenBarrier = OpenBarrier("IN");

                                    isSuccessTurnOnGreenTrafficLight = TurnOnGreenTrafficLight("IN");
                                }
                                else if (isLuongRa)
                                {
                                    tmpCardNoLst_Out.Add(newCardNoLog);

                                    isSuccessOpenBarrier = OpenBarrier("OUT");

                                    isSuccessTurnOnGreenTrafficLight = TurnOnGreenTrafficLight("OUT");
                                }

                                if (isSuccessTurnOnGreenTrafficLight)
                                {
                                    _gatewayLogger.LogInfo($"6. Bat den xanh thanh cong");
                                }
                                else
                                {
                                    _gatewayLogger.LogInfo($"6. Bat den xanh KHONG thanh cong");
                                }

                                if (isSuccessOpenBarrier)
                                {
                                    _gatewayLogger.LogInfo($"7. Mo barrier thanh cong");
                                    _gatewayLogger.LogInfo($"8. Ghi log thiet bi mo barrier");

                                    string luongText = isLuongVao ? "vào" : "ra";
                                    string deviceCode = isLuongVao ? "BV.M221.BRE-1" : "BV.M221.BRE-2";
                                    var newLog = new CategoriesDevicesLogItemResponse
                                    {
                                        Code = deviceCode,
                                        ActionType = 1,
                                        ActionInfo = $"Mở barrier cho xe {currentOrder.Vehicle} {luongText}, theo đơn hàng {deliveryCodes}",
                                        ActionDate = DateTime.Now,
                                    };

                                    await _categoriesDevicesLogRepository.CreateAsync(newLog);
                                }
                                else
                                {
                                    _gatewayLogger.LogInfo($"7. Mo barrier KHONG thanh cong");
                                }

                                _gatewayLogger.LogInfo($"Ket thuc.");
                            }
                            else
                            {
                                _gatewayLogger.LogInfo($"5. Update don hang KHONG thanh cong => Ket thuc.");
                            }
                        }
                    }
                }
            }
        }

        public bool OpenBarrier(string luong)
        {
            return true;
            int portNumberDeviceIn = luong == "IN" ? (int)barrierVao.PortNumberDeviceIn : (int)barrierRa.PortNumberDeviceIn;
            int portNumberDeviceOut = luong == "IN" ? (int)barrierVao.PortNumberDeviceOut : (int)barrierRa.PortNumberDeviceOut;

            return _barrier.TurnOn(m221.IpAddress, (int)m221.PortNumber, portNumberDeviceIn, portNumberDeviceOut);
        }

        public bool TurnOnGreenTrafficLight(string luong)
        {
            return true;
            if (trafficLightVao == null || trafficLightRa == null)
            {
                return false;
            }

            string ipAddress = luong == "IN" ? trafficLightVao.IpAddress : trafficLightRa.IpAddress;

            _trafficLight.Connect($"{ipAddress}");

            return _trafficLight.TurnOnGreenOffRed();
        }
    }
}
