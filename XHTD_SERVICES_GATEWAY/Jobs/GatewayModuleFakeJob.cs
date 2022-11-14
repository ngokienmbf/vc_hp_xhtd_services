﻿using System;
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

        protected readonly Barrier _barrier;

        protected readonly TCPTrafficLight _trafficLight;

        protected readonly Notification _notification;

        protected readonly GatewayLogger _gatewayLogger;

        private IntPtr h21 = IntPtr.Zero;

        private static bool DeviceConnected = false;

        private List<CardNoLog> tmpCardNoLst_In = new List<CardNoLog>();

        private List<CardNoLog> tmpCardNoLst_Out = new List<CardNoLog>();

        private tblCategoriesDevice c3400, rfidRa1, rfidRa2, rfidVao1, rfidVao2, m221, barrierVao, barrierRa, trafficLightVao, trafficLightRa;

        private string HubURL;

        private string HubValue;

        private bool IsJustReceivedHubData = false;

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
                _gatewayLogger.LogInfo("start gateway service");
                _gatewayLogger.LogInfo("----------------------------");

                HandleHubConnection();

                // Get devices info
                await LoadDevicesInfo();

                AuthenticateGatewayModule();
            });                                                                                                                     
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

            Connection.On<string>("SendOffersToUser", data =>
            {
                var fakeHubResponse = JsonConvert.DeserializeObject<FakeHubResponse>(data);
                if (fakeHubResponse != null && fakeHubResponse.RFIDData != null && fakeHubResponse.RFIDData != "") { 
                    IsJustReceivedHubData = true;
                    HubValue = fakeHubResponse.RFIDData;
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
             * == Dùng chung cho cả cổng ra và cổng vào == 
             * 1. Connect Device C3-400
             * 2. Đọc dữ liệu từ thiết bị C3-400
             * 3. Lấy ra cardNo từ dữ liệu đọc được => cardNoCurrent. 
             * * 3.1. Xác định xe vào hay ra cổng theo gia tri door từ C3-400
             * * 3.2. Loại bỏ các cardNoCurrent đã, đang xử lý (đã check trước đó)
             * * 3.3. Kiểm tra cardNoCurrent có hợp lệ hay không
             * * 3.4. Kiểm tra cardNoCurrent có đang chứa đơn hàng hợp lệ không
             * * 3.5. Cập nhật đơn hàng: Step
             * * 3.6. Bật đèn xanh giao thông, mở barrier
             * * 3.7. Ghi log thiết bị
             * * 3.8. Bắn tín hiệu thông báo
             * * 3.9. Hiển thị led
             */

            // 1. Connect Device
            while (!DeviceConnected)
            {
                ConnectGatewayModule();
            }

            // 2. Đọc dữ liệu từ thiết bị
            ReadDataFromC3400();

        }

        public async Task LoadDevicesInfo()
        {
            var devices = await _categoriesDevicesRepository.GetDevices("BV");

            c3400 = devices.FirstOrDefault(x => x.Code == "BV.C3-400");
            rfidRa1 = devices.FirstOrDefault(x => x.Code == "BV.C3-400.RFID.RA-1");
            rfidRa2 = devices.FirstOrDefault(x => x.Code == "BV.C3-400.RFID.RA-2");
            rfidVao1 = devices.FirstOrDefault(x => x.Code == "BV.C3-400.RFID.VAO-1");
            rfidVao2 = devices.FirstOrDefault(x => x.Code == "BV.C3-400.RFID.VAO-2");

            m221 = devices.FirstOrDefault(x => x.Code == "BV.M221");
            barrierVao = devices.FirstOrDefault(x => x.Code == "BV.M221.BRE-1");
            barrierRa = devices.FirstOrDefault(x => x.Code == "BV.M221.BRE-2");
            trafficLightVao = devices.FirstOrDefault(x => x.Code == "BV.DGT-1");
            trafficLightRa = devices.FirstOrDefault(x => x.Code == "BV.DGT-2");
        }

        public bool ConnectGatewayModule()
        {
            _gatewayLogger.LogInfo("start connect to C3-400 ... ");

            _gatewayLogger.LogInfo("connected");

            DeviceConnected = true;
                    
            return DeviceConnected;
        }

        public async void ReadDataFromC3400()
        {
            _gatewayLogger.LogInfo("start read data from C3-400 ...");

            if (DeviceConnected)
            {
                while (DeviceConnected)
                {
                    string str;
                    string[] tmp = null;

                    if (IsJustReceivedHubData)
                    {
                        IsJustReceivedHubData = false;

                        str = HubValue;
                        tmp = str.Split(',');

                        // Trường hợp bắt được tag RFID
                        if (tmp[2] != "0" && tmp[2] != "") {

                            // 3. Lấy ra cardNo từ dữ liệu đọc được => cardNoCurrent
                            var cardNoCurrent = tmp[2]?.ToString();
                            var doorCurrent = tmp[3]?.ToString();

                            _gatewayLogger.LogInfo("----------------------------");
                            _gatewayLogger.LogInfo($"Tag {cardNoCurrent} door {doorCurrent} ... ");

                            // 3.1.Xác định xe vào hay ra cổng theo gia tri door từ C3-400
                            var isLuongVao = doorCurrent == rfidVao1.PortNumberDeviceIn.ToString()
                                            || doorCurrent == rfidVao2.PortNumberDeviceIn.ToString();

                            var isLuongRa = doorCurrent == rfidRa1.PortNumberDeviceIn.ToString()
                                            || doorCurrent == rfidRa2.PortNumberDeviceIn.ToString();

                            // 3.2.Loại bỏ các cardNoCurrent đã, đang xử lý(đã check trước đó)
                            if (isLuongVao) {
                                if (tmpCardNoLst_In.Count > 5) tmpCardNoLst_In.RemoveRange(0, 4);

                                if (tmpCardNoLst_In.Exists(x => x.CardNo.Equals(cardNoCurrent) && x.DateTime > DateTime.Now.AddMinutes(-1)))
                                {
                                    _gatewayLogger.LogInfo($@"1. Tag {cardNoCurrent} da duoc xu ly => Ket thuc.");

                                    continue;
                                }
                            }
                            else if (isLuongRa)
                            {
                                if (tmpCardNoLst_Out.Count > 5) tmpCardNoLst_Out.RemoveRange(0, 4);

                                if (tmpCardNoLst_Out.Exists(x => x.CardNo.Equals(cardNoCurrent) && x.DateTime > DateTime.Now.AddMinutes(-1)))
                                {
                                    _gatewayLogger.LogInfo($@"1. Tag {cardNoCurrent} da duoc xu ly => Ket thuc.");

                                    continue;
                                }
                            }

                            // 3.3. Kiểm tra cardNoCurrent có hợp lệ hay không
                            _gatewayLogger.LogInfo($"1. Kiem tra tag {cardNoCurrent} hop le: ");

                            bool isValid = _rfidRepository.CheckValidCode(cardNoCurrent);

                            if (isValid)
                            {
                                _gatewayLogger.LogInfo($"CO");
                            }
                            else
                            {
                                _gatewayLogger.LogInfo($"KHONG => Ket thuc.");

                                _notification.SendNotification("GETWAY", null, null, cardNoCurrent, null, "Không xác định phương tiện");

                                // Cần add các thẻ invalid vào 1 mảng để tránh phải check lại
                                // Chỉ check lại các invalid tag sau 1 khoảng thời gian: 3 phút

                                continue;
                            }

                            // 3.4. Kiểm tra cardNoCurrent có đang chứa đơn hàng hợp lệ không
                            _gatewayLogger.LogInfo($"2. Kiem tra tag {cardNoCurrent} co don hang hop le: ");

                            List <tblStoreOrderOperating> currentOrders = null;
                            if (isLuongVao)
                            {
                                currentOrders = await _storeOrderOperatingRepository.GetCurrentOrdersEntraceGatewayByCardNoReceiving(cardNoCurrent);
                            }
                            else if (isLuongRa){
                                currentOrders = await _storeOrderOperatingRepository.GetCurrentOrdersExitGatewayByCardNoReceiving(cardNoCurrent);
                            }

                            if (currentOrders == null || currentOrders.Count == 0) {

                                _gatewayLogger.LogInfo($"KHONG => Ket thuc.");

                                _notification.SendNotification("GETWAY", null, null, cardNoCurrent, null, "Không xác định đơn hàng hợp lệ");

                                continue; 
                            }

                            var currentOrder = currentOrders.FirstOrDefault();
                            var deliveryCodes = String.Join(";", currentOrders.Select(x => x.DeliveryCode).ToArray());

                            _gatewayLogger.LogInfo($"CO. DeliveryCode = {deliveryCodes}");

                            // 3.5. Cập nhật đơn hàng
                            _gatewayLogger.LogInfo($"3. Tien hanh update don hang: ");

                            var isUpdatedOrder = false;

                            if (isLuongVao)
                            {
                                _gatewayLogger.LogInfo($"vao cong");

                                isUpdatedOrder = await _storeOrderOperatingRepository.UpdateOrderEntraceGateway(cardNoCurrent);
                            }
                            else if (isLuongRa)
                            {
                                _gatewayLogger.LogInfo($"ra cong");

                                isUpdatedOrder = await _storeOrderOperatingRepository.UpdateOrderExitGateway(cardNoCurrent);
                            }

                            if (isUpdatedOrder)
                            {
                                /*
                                    * 3.6. Bật đèn xanh giao thông, mở barrier
                                    * 3.7. Ghi log thiết bị
                                    * 3.8. Bắn tín hiệu thông báo
                                    */

                                _gatewayLogger.LogInfo($"4. Update don hang thanh cong.");

                                var newCardNoLog = new CardNoLog { CardNo = cardNoCurrent, DateTime = DateTime.Now };

                                if (isLuongVao)
                                {
                                    tmpCardNoLst_In.Add(newCardNoLog);

                                    // Mở barrier
                                    _gatewayLogger.LogInfo($"5. Mo barrier vao");

                                    OpenBarrier("VAO", "BV.M221.BRE-1", currentOrder.Vehicle, deliveryCodes);

                                    // Bật đèn xanh giao thông
                                    _gatewayLogger.LogInfo($"6. Bat den xanh vao");

                                    OpenTrafficLight("VAO");
                                }
                                else if (isLuongRa)
                                {
                                    tmpCardNoLst_Out.Add(newCardNoLog);

                                    // Mở barrier
                                    _gatewayLogger.LogInfo($"5. Mo barrier ra");

                                    OpenBarrier("RA", "BV.M221.BRE-2", currentOrder.Vehicle, deliveryCodes);

                                    // Bật đèn xanh giao thông
                                    _gatewayLogger.LogInfo($"6. Bat den xanh ra");

                                    OpenTrafficLight("RA");
                                }
                            }
                            else
                            {
                                _gatewayLogger.LogInfo($"4. Update don hang KHONG thanh cong => Ket thuc.");
                            }
                        }
                    }
                }
            }
        }

        public async void OpenBarrier(string luong, string code, string vehicle, string deliveryCode)
        {
            string luongText = luong == "VAO" ? "vào" : "ra";
            int portNumberDeviceIn = luong == "VAO" ? (int)barrierVao.PortNumberDeviceIn : (int)barrierRa.PortNumberDeviceIn;
            int portNumberDeviceOut = luong == "VAO" ? (int)barrierVao.PortNumberDeviceOut : (int)barrierRa.PortNumberDeviceOut;

            var newLog = new CategoriesDevicesLogItemResponse
            {
                Code = code,
                ActionType = 1,
                ActionInfo = $"Mở barrier cho xe {vehicle} {luongText}, theo đơn hàng {deliveryCode}",
                ActionDate = DateTime.Now,
            };

            var isOpenSuccess = _barrier.TurnOn(m221.IpAddress, (int)m221.PortNumber, portNumberDeviceIn, portNumberDeviceOut);
            if (isOpenSuccess)
            {
                await _categoriesDevicesLogRepository.CreateAsync(newLog);
            }
        }

        public void OpenTrafficLight(string luong)
        {
            string ipAddress = luong == "VAO" ? trafficLightVao.IpAddress : trafficLightRa.IpAddress;

            _trafficLight.Connect($"{ipAddress}");
            var isSuccess = _trafficLight.TurnOnGreenOffRed();
            if (isSuccess)
            {
                _gatewayLogger.LogInfo("6.1. Open TrafficLight: OK");
            }
            else
            {
                _gatewayLogger.LogInfo("6.1. Open TrafficLight: Failed");
            }
        }
    }
}