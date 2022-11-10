﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Quartz;
using log4net;
using XHTD_SERVICES.Data.Repositories;
using RestSharp;
using XHTD_SERVICES_TRAM951.Models.Response;
using XHTD_SERVICES.Data.Models.Response;
using XHTD_SERVICES_TRAM951.Models.Request;
using Newtonsoft.Json;
using System.Configuration;
using System.Collections.Specialized;
using XHTD_SERVICES_TRAM951.Models.Values;
using System.Runtime.InteropServices;
using XHTD_SERVICES.Device.PLCM221;
using XHTD_SERVICES.Device;
using XHTD_SERVICES.Data.Models.Values;
using XHTD_SERVICES.Data.Entities;

namespace XHTD_SERVICES_TRAM951.Jobs
{
    public class Tram951ModuleJob : IJob
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        protected readonly StoreOrderOperatingRepository _storeOrderOperatingRepository;

        protected readonly RfidRepository _rfidRepository;

        protected readonly CategoriesDevicesRepository _categoriesDevicesRepository;

        protected readonly CategoriesDevicesLogRepository _categoriesDevicesLogRepository;

        protected readonly Barrier _barrier;

        protected readonly TCPTrafficLight _trafficLight;

        protected readonly Sensor _sensor;

        private IntPtr h21 = IntPtr.Zero;

        private static bool DeviceConnected = false;

        private M221Result PLC_Result;

        private List<CardNoLog> tmpCardNoLst_In = new List<CardNoLog>();

        private List<CardNoLog> tmpCardNoLst_Out = new List<CardNoLog>();

        private tblCategoriesDevice c3400, rfidRa1, rfidRa2, rfidVao1, rfidVao2, m221, barrierVao, barrierRa, trafficLightVao, trafficLightRa, sensor1, sensor2;

        [DllImport(@"C:\Windows\System32\plcommpro.dll", EntryPoint = "Connect")]
        public static extern IntPtr Connect(string Parameters);

        [DllImport(@"C:\Windows\System32\plcommpro.dll", EntryPoint = "PullLastError")]
        public static extern int PullLastError();

        [DllImport(@"C:\Windows\System32\plcommpro.dll", EntryPoint = "GetRTLog")]
        public static extern int GetRTLog(IntPtr h, ref byte buffer, int buffersize);

        public Tram951ModuleJob(
            StoreOrderOperatingRepository storeOrderOperatingRepository, 
            RfidRepository rfidRepository,
            CategoriesDevicesRepository categoriesDevicesRepository,
            CategoriesDevicesLogRepository categoriesDevicesLogRepository,
            Barrier barrier,
            TCPTrafficLight trafficLight,
            Sensor sensor
            )
        {
            _storeOrderOperatingRepository = storeOrderOperatingRepository;
            _rfidRepository = rfidRepository;
            _categoriesDevicesRepository = categoriesDevicesRepository;
            _categoriesDevicesLogRepository = categoriesDevicesLogRepository;
            _barrier = barrier;
            _trafficLight = trafficLight;
            _sensor = sensor;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            await Task.Run(async () =>
            {
                Console.WriteLine("start tram951 service");
                Console.WriteLine("----------------------------");

                log.Info("start tram951 service");
                log.Info("----------------------------");

                // Get devices info
                await LoadDevicesInfo();

                AuthenticateTram951Module();
            });
        }

        public void AuthenticateTram951Module()
        {
            /*
             * Áp dụng tại trạm cân, dùng chung cho cả cân ra và cân vào ở mỗi bàn cân
             * 1. Connect Device C3-400
             * 2. Đọc dữ liệu từ thiết bị C3-400
             * 3. Lấy ra cardNo từ dữ liệu đọc được => cardNoCurrent. 
             * Khi đọc được tag thì nghĩa là xe đã lên bàn cân
             * 4. Kiểm tra cardNoCurrent có hợp lệ hay không
             * 5. Kiểm tra cardNoCurrent có đang chứa đơn hàng hợp lệ không
             * 6. Xác định xe cân vao hay cân ra theo gia tri door từ C3-400
             * 7. Kiểm tra xe có vi phạm cảm biến
             * 8. Bắt đầu lấy giá trị từ cân. Kiểm tra giá trị cân ổn định
             * 9. Bật đèn đỏ
             * 10. Đóng barrier
             * 11. Cân vào: 
             *     +  Gọi api cân để tiến hàng cân vào đối với đơn đặt hàng đang xử lý, 
             *     cập nhật khối lượng cân, bước xử lý của đơn hàng trong CSDL,
             *     vào khối lượng không tải của phương tiện;
             *     Cân ra: 
             *     + Gọi api cân để tiến hàng cân ra đối với đơn đặt hàng đang xử lý, 
             *     cập nhật khối lượng cân, bước xử lý của đơn hàng trong CSDL;
             * 10. Bật đèn xanh
             * 11. Mở barrier để xe rời bàn cân
             * 12. Cân vào
             *     + Tiến hành xếp số thứ tự vào máng xuất lấy hàng của xe vừa cân vào xong;
             *     Cân ra:
             *     + Đánh dấu trạng thái đơn hàng (step = 7) và gửi thông tin ra cổng bảo vệ;
             */

            // 1. Connect Device
            while (!DeviceConnected)
            {
                ConnectTram951Module();
            }

            // 2. Đọc dữ liệu từ thiết bị
            ReadDataFromC3400();

        }

        public async Task LoadDevicesInfo()
        {
            var devices = await _categoriesDevicesRepository.GetDevices("951");

            c3400 = devices.FirstOrDefault(x => x.Code == "951.C3-400-1");
            rfidRa1 = devices.FirstOrDefault(x => x.Code == "951.C3-400-1.RFID.RA-1");
            rfidRa2 = devices.FirstOrDefault(x => x.Code == "951.C3-400-1.RFID.RA-2");
            rfidVao1 = devices.FirstOrDefault(x => x.Code == "951.C3-400-1.RFID.VAO-1");
            rfidVao2 = devices.FirstOrDefault(x => x.Code == "951.C3-400-1.RFID.VAO-2");

            m221 = devices.FirstOrDefault(x => x.Code == "951.M221");
            barrierVao = devices.FirstOrDefault(x => x.Code == "951.M221.BRE-1-VAO");
            barrierRa = devices.FirstOrDefault(x => x.Code == "951.M221.BRE-1-RA");
            trafficLightVao = devices.FirstOrDefault(x => x.Code == "951.M221.DGT-1");
            trafficLightRa = devices.FirstOrDefault(x => x.Code == "951.M221.DGT-2");
            sensor1 = devices.FirstOrDefault(x => x.Code == "951.M221.SENSOR-1");
            sensor2 = devices.FirstOrDefault(x => x.Code == "951.M221.SENSOR-2");
        }

        public bool ConnectTram951Module()
        {
            Console.Write("start connect to C3-400 ... ");
            log.Info("start connect to C3-400 ... ");

            try
            {
                string str = $"protocol=TCP,ipaddress={c3400?.IpAddress},port={c3400?.PortNumber},timeout=2000,passwd=";
                int ret = 0;
                if (IntPtr.Zero == h21)
                {
                    h21 = Connect(str);
                    if (h21 != IntPtr.Zero)
                    {
                        Console.WriteLine("connected");
                        log.Info("connected");

                        DeviceConnected = true;
                    }
                    else
                    {
                        Console.WriteLine("connected failed");
                        log.Info("connected failed");

                        ret = PullLastError();
                        DeviceConnected = false;
                    }
                }
                return DeviceConnected;
            }
            catch (Exception ex)
            {
                Console.WriteLine($@"ConnectTram951Module : {ex.Message}");
                log.Error($@"ConnectTram951Module : {ex.StackTrace}");

                return false;
            }
        }

        public async void ReadDataFromC3400()
        {
            Console.WriteLine("start read data from C3-400 ...");
            log.Info("start read data from C3-400 ...");

            if (DeviceConnected)
            {
                while (DeviceConnected)
                {
                    int ret = 0, buffersize = 256;
                    string str = "";
                    string[] tmp = null;
                    byte[] buffer = new byte[256];

                    if (IntPtr.Zero != h21)
                    {
                        ret = GetRTLog(h21, ref buffer[0], buffersize);
                        if (ret >= 0)
                        {
                            str = Encoding.Default.GetString(buffer);
                            tmp = str.Split(',');

                            // Trường hợp bắt được tag RFID
                            if (tmp[2] != "0" && tmp[2] != "") {

                                // 1. Lấy ra cardNo từ dữ liệu đọc được => cardNoCurrent
                                var cardNoCurrent = tmp[2]?.ToString();
                                var doorCurrent = tmp[3]?.ToString();

                                var isLuongVao = doorCurrent == rfidVao1.PortNumberDeviceIn.ToString()
                                                || doorCurrent == rfidVao2.PortNumberDeviceIn.ToString();

                                var isLuongRa = doorCurrent == rfidRa1.PortNumberDeviceIn.ToString()
                                                || doorCurrent == rfidRa2.PortNumberDeviceIn.ToString();

                                Console.WriteLine("----------------------------");
                                Console.WriteLine($"Tag {cardNoCurrent} door {doorCurrent} ... ");

                                log.Info("----------------------------");
                                log.Info($"Tag {cardNoCurrent} door {doorCurrent} ... ");

                                // Luồng vào
                                if(isLuongVao) {
                                    if (tmpCardNoLst_In.Count > 5) tmpCardNoLst_In.RemoveRange(0, 4);

                                    if (tmpCardNoLst_In.Exists(x => x.CardNo.Equals(cardNoCurrent) && x.DateTime > DateTime.Now.AddMinutes(-1)))
                                    {
                                        Console.WriteLine($@"1. Tag {cardNoCurrent} da duoc xu ly => Ket thuc.");
                                        log.Info($@"1. Tag {cardNoCurrent} da duoc xu ly => Ket thuc.");

                                        continue;
                                    }
                                }
                                // Luồng ra
                                else if (isLuongRa)
                                {
                                    if (tmpCardNoLst_Out.Count > 5) tmpCardNoLst_Out.RemoveRange(0, 4);

                                    if (tmpCardNoLst_Out.Exists(x => x.CardNo.Equals(cardNoCurrent) && x.DateTime > DateTime.Now.AddMinutes(-1)))
                                    {
                                        Console.WriteLine($@"1. Tag {cardNoCurrent} da duoc xu ly => Ket thuc.");
                                        log.Info($@"1. Tag {cardNoCurrent} da duoc xu ly => Ket thuc.");

                                        continue;
                                    }
                                }

                                // 2. Kiểm tra cardNoCurrent có tồn tại trong hệ thống RFID hay ko 
                                Console.Write($"1. Kiem tra tag {cardNoCurrent} hop le: ");
                                log.Info($"1. Kiem tra tag {cardNoCurrent} hop le: ");

                                bool isValid = _rfidRepository.CheckValidCode(cardNoCurrent);

                                if (isValid)
                                {
                                    Console.WriteLine($"CO");
                                    log.Info($"CO");
                                }
                                else
                                {
                                    Console.WriteLine($"KHONG => Ket thuc.");
                                    log.Info($"KHONG => Ket thuc.");

                                    // Cần add các thẻ invalid vào 1 mảng để tránh phải check lại
                                    // Chỉ check lại các invalid tag sau 1 khoảng thời gian: 3 phút

                                    continue;
                                }

                                // 3. Kiểm tra cardNoCurrent có đang chứa đơn hàng hợp lệ không
                                Console.Write($"2. Kiem tra tag {cardNoCurrent} co don hang hop le: ");
                                log.Info($"2. Kiem tra tag {cardNoCurrent} co don hang hop le: ");

                                tblStoreOrderOperating orderCurrent = null;
                                if (isLuongVao)
                                {
                                    orderCurrent = _storeOrderOperatingRepository.GetCurrentOrderEntraceGatewayByCardNoReceiving(cardNoCurrent);
                                }
                                else if (isLuongRa){
                                    orderCurrent = _storeOrderOperatingRepository.GetCurrentOrderExitGatewayByCardNoReceiving(cardNoCurrent);
                                }

                                if (orderCurrent == null) {

                                    Console.WriteLine($"KHONG => Ket thuc.");
                                    log.Info($"KHONG => Ket thuc.");

                                    continue; 
                                }
                                else
                                {
                                    Console.WriteLine($"CO. DeliveryCode = {orderCurrent.DeliveryCode}");
                                    log.Info($"CO. DeliveryCode = {orderCurrent.DeliveryCode}");
                                }

                                /*
                                 * 4. Kiểm tra xe có vi phạm cảm biến
                                 */
                                var isValidSensor = CheckValidSensor();
                                if (isValidSensor)
                                {

                                }
                                else
                                {
                                    // Vi phạm cảm biến
                                    continue;
                                }

                                /* 4. Cập nhật đơn hàng
                                 * Luồng vào - ra
                                 * Xác định theo doorId của RFID: biết tag do anten nào nhận diện thì biết dc là xe ra hay vào
                                 * Hoặc theo Step của đơn hàng
                                 */

                                Console.Write($"3. Tien hanh update don hang: ");
                                log.Info($"3. Tien hanh update don hang: ");

                                var isUpdatedOrder = false;

                                // Luồng vào
                                if (isLuongVao)
                                {
                                    Console.WriteLine($"vao cong");
                                    log.Info($"vao cong");

                                    isUpdatedOrder = await _storeOrderOperatingRepository.UpdateOrderEntraceGateway(cardNoCurrent);
                                }
                                // Luồng ra
                                else if (isLuongRa)
                                {
                                    Console.WriteLine($"ra cong");
                                    log.Info($"ra cong");

                                    isUpdatedOrder = await _storeOrderOperatingRepository.UpdateOrderExitGateway(cardNoCurrent);
                                }

                                if (isUpdatedOrder)
                                {
                                    /*
                                     * Tắt đèn đỏ
                                     * Bật đèn xanh
                                     * Mở barrier
                                     * Ghi log thiết bị
                                     */

                                    Console.WriteLine($"4. Update don hang thanh cong.");
                                    log.Info($"4. Update don hang thanh cong.");

                                    var newCardNoLog = new CardNoLog { CardNo = cardNoCurrent, DateTime = DateTime.Now };

                                    if (isLuongVao)
                                    {
                                        tmpCardNoLst_In.Add(newCardNoLog);

                                        // Mở barrier
                                        Console.WriteLine($"5. Mo barrier vao");
                                        log.Info($"5. Mo barrier vao");

                                        OpenBarrier("VAO", "BV.M221.BRE-1", orderCurrent.Vehicle, orderCurrent.DeliveryCode);

                                        // Bật đèn xanh giao thông
                                        Console.WriteLine($"6. Bat den xanh vao");
                                        log.Info($"6. Bat den xanh vao");

                                        OpenTrafficLight("VAO");
                                    }
                                    else if (isLuongRa)
                                    {
                                        tmpCardNoLst_Out.Add(newCardNoLog);

                                        // Mở barrier
                                        Console.WriteLine($"5. Mo barrier ra");
                                        log.Info($"5. Mo barrier ra");

                                        OpenBarrier("RA", "BV.M221.BRE-2", orderCurrent.Vehicle, orderCurrent.DeliveryCode);

                                        // Bật đèn xanh giao thông
                                        Console.WriteLine($"6. Bat den xanh ra");
                                        log.Info($"6. Bat den xanh ra");

                                        OpenTrafficLight("RA");
                                    }
                                }
                                else
                                {
                                    Console.WriteLine($"4. Update don hang KHONG thanh cong => Ket thuc.");
                                    log.Info($"4. Update don hang KHONG thanh cong => Ket thuc.");
                                }
                            }
                        }
                        else
                        {
                            log.Warn("Lỗi không đọc được dữ liệu, có thể do mất kết nối");
                            DeviceConnected = false;
                            h21 = IntPtr.Zero;

                            AuthenticateTram951Module();
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

            PLC_Result = _barrier.Connect($"{m221.IpAddress}", (int)m221.PortNumber);

            if (PLC_Result == M221Result.SUCCESS)
            {
                Console.WriteLine($"5.1. Connected to PLC ... {_barrier.GetLastErrorString()}");
                log.Info($"5.1. Connected to PLC ... {_barrier.GetLastErrorString()}");

                bool[] Ports = new bool[24];
                PLC_Result = _barrier.CheckInputPorts(Ports);

                if (PLC_Result == M221Result.SUCCESS)
                {
                    if (!Ports[portNumberDeviceIn])
                    {
                        PLC_Result = _barrier.ShuttleOutputPort((byte.Parse(portNumberDeviceOut.ToString())));
                        if (PLC_Result == M221Result.SUCCESS)
                        {
                            await _categoriesDevicesLogRepository.CreateAsync(newLog);

                            Console.WriteLine("5.2. Open barrier: OK");
                            log.Info("5.2. Open barrier: OK");
                        }
                        else
                        {
                            Console.WriteLine("5.2. Open barrier: ERROR");
                            log.Info("5.2. Open barrier: ERROR");
                        }
                    }
                }
            }
            else
            {
                Console.WriteLine($"5.1. Connect failed to PLC ... {_barrier.GetLastErrorString()}");
                log.Info($"5.1. Connect failed to PLC ... {_barrier.GetLastErrorString()}");
            }
        }

        public bool CheckValidSensor()
        {
            int portNumberDeviceIn1 = (int)sensor1.PortNumberDeviceIn;
            int portNumberDeviceIn2 = (int)sensor2.PortNumberDeviceIn;

            PLC_Result = _sensor.Connect($"{m221.IpAddress}", (int)m221.PortNumber);

            if (PLC_Result == M221Result.SUCCESS)
            {
                bool[] Ports = new bool[24];
                PLC_Result = _barrier.CheckInputPorts(Ports);

                if (PLC_Result == M221Result.SUCCESS)
                {
                    if (Ports[portNumberDeviceIn1] || Ports[portNumberDeviceIn2])
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }

                return true;
            }
            else
            {
                return false;
            }
        }

        public void OpenTrafficLight(string luong)
        {
            string ipAddress = luong == "VAO" ? trafficLightVao.IpAddress : trafficLightRa.IpAddress;

            _trafficLight.Connect($"{ipAddress}");
            var isSuccess = _trafficLight.TurnOnGreenOffRed();
            if (isSuccess)
            {
                Console.WriteLine("6.1. Open TrafficLight: OK");
                log.Info("5.2. Open TrafficLight: OK");
            }
            else
            {
                Console.WriteLine("6.1. Open TrafficLight: Failed");
                log.Info("5.2. Open TrafficLight: Failed");
            }
        }
    }
}
