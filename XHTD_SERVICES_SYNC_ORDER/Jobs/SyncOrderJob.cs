using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Quartz;
using log4net;
using XHTD_SERVICES.Data.Repositories;
using RestSharp;
using XHTD_SERVICES_SYNC_ORDER.Models.Response;
using XHTD_SERVICES.Data.Models.Response;
using Newtonsoft.Json;
using XHTD_SERVICES_SYNC_ORDER.Models.Values;
using XHTD_SERVICES.Helper;
using XHTD_SERVICES.Helper.Models.Request;
using System.Threading;
using XHTD_SERVICES.Data.Entities;

namespace XHTD_SERVICES_SYNC_ORDER.Jobs
{
    public class SyncOrderJob : IJob
    {
        protected readonly StoreOrderOperatingRepository _storeOrderOperatingRepository;

        protected readonly VehicleRepository _vehicleRepository;

        protected readonly SystemParameterRepository _systemParameterRepository;

        protected readonly Notification _notification;

        protected readonly SyncOrderLogger _syncOrderLogger;

        private static string strToken;

        protected const string SYNC_ORDER_ACTIVE = "SYNC_ORDER_ACTIVE";

        protected const string SYNC_ORDER_HOURS = "SYNC_ORDER_HOURS";

        private static bool isActiveService = true;

        private static int numberHoursSearchOrder = 48;

        public SyncOrderJob(
            StoreOrderOperatingRepository storeOrderOperatingRepository,
            VehicleRepository vehicleRepository,
            SystemParameterRepository systemParameterRepository,
            Notification notification,
            SyncOrderLogger syncOrderLogger
            )
        {
            _storeOrderOperatingRepository = storeOrderOperatingRepository;
            _vehicleRepository = vehicleRepository;
            _systemParameterRepository = systemParameterRepository;
            _notification = notification;
            _syncOrderLogger = syncOrderLogger;
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
                    _syncOrderLogger.LogInfo("Service dong bo don hang dang TAT.");
                    return;
                }

                await SyncOrderProcess();
            });
        }

        public async Task LoadSystemParameters()
        {
            var parameters = await _systemParameterRepository.GetSystemParameters();

            var activeParameter = parameters.FirstOrDefault(x => x.Code == SYNC_ORDER_ACTIVE);
            var numberHoursParameter = parameters.FirstOrDefault(x => x.Code == SYNC_ORDER_HOURS);

            if(activeParameter == null || activeParameter.Value == "0")
            {
                isActiveService = false;
            }

            if (numberHoursParameter != null)
            {
                numberHoursSearchOrder = Convert.ToInt32(numberHoursParameter.Value);
            }
        }

        public async Task SyncOrderProcess()
        {
            _syncOrderLogger.LogInfo("start process SyncOrderJob");

            GetToken();

            List<OrderItemResponse> websaleOrders = GetWebsaleOrder();

            if (websaleOrders == null || websaleOrders.Count == 0)
            {
                return;
            }

            bool isChanged = false;

            foreach (var websaleOrder in websaleOrders)
            {
                bool isSynced = await SyncWebsaleOrderToDMS(websaleOrder);

                if (!isChanged) isChanged = isSynced;
            }

            if (isChanged)
            {
                _notification.SendNotification(
                    "SYNC_ORDER",
                    null,
                    1,
                    "Đồng bộ đơn hàng thành công",
                    0,
                    null,
                    null,
                    0,
                    null,
                    null,
                    null
                );
            }
        }

        public void GetToken()
        {
            try
            {
                IRestResponse response = HttpRequest.GetWebsaleToken();

                var content = response.Content;

                var responseData = JsonConvert.DeserializeObject<GetTokenResponse>(content);
                strToken = responseData.access_token;
            }
            catch (Exception ex)
            {
                _syncOrderLogger.LogInfo("getToken error: " + ex.Message);
            }
        }

        public List<OrderItemResponse> GetWebsaleOrder()
        {
            IRestResponse response = HttpRequest.GetWebsaleOrder(strToken, numberHoursSearchOrder);
            var content = response.Content;

            if (response.StatusDescription.Equals("Unauthorized"))
            {
                _syncOrderLogger.LogInfo("Unauthorized GetWebsaleOrder");

                return null;
            }

            var responseData = JsonConvert.DeserializeObject<SearchOrderResponse>(content);

            return responseData.collection.OrderBy(x => x.id).ToList();
        }

        public async Task<bool> SyncWebsaleOrderToDMS(OrderItemResponse websaleOrder)
        {
            bool isSynced = false;

            var stateId = 0;
            switch (websaleOrder.status.ToUpper())
            {
                case "BOOKED":
                    switch (websaleOrder.orderPrintStatus.ToUpper())
                    {
                        case "BOOKED":
                        case "APPROVED":
                        case "PENDING":
                            stateId = (int)OrderState.DA_XAC_NHAN;
                            break;
                        case "PRINTED":
                            stateId = (int)OrderState.DA_IN_PHIEU;
                            break;
                    }
                    break;
                case "PRINTED":
                    stateId = (int)OrderState.DA_IN_PHIEU;
                    break;
                case "VOIDED":
                    stateId = (int)OrderState.DA_HUY;
                    break;
                case "RECEIVING":
                    stateId = (int)OrderState.DANG_LAY_HANG;
                    break;
                case "RECEIVED":
                    stateId = (int)OrderState.DA_XUAT_HANG;
                    break;
            }

            if (stateId != (int)OrderState.DA_HUY && stateId != (int)OrderState.DA_XUAT_HANG)
            {
                isSynced = await _storeOrderOperatingRepository.CreateAsync(websaleOrder);

                /*
                 * Đơn hàng mới
                 * Kiểm tra biển số xe trong đơn hàng đã có trong bảng phương tiện (tblVehicle), 
                 * nếu chưa có thì thêm biển số xe vào bảng phương tiện
                 */ 
                if (isSynced)
                {
                    var vehicleCode = websaleOrder.vehicleCode.Replace("-", "").Replace("  ", "").Replace(" ", "").Replace("/", "").Replace(".", "").ToUpper();
                    await _vehicleRepository.CreateAsync(vehicleCode);
                }
            }
            else if (stateId == (int)OrderState.DA_HUY){
                isSynced = await _storeOrderOperatingRepository.CancelOrder(websaleOrder.id);
            }

            return isSynced;
        }
    }
}
