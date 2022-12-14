using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;
using XHTD_SERVICES.Data.Entities;
using XHTD_SERVICES.Data.Models.Response;
using log4net;

namespace XHTD_SERVICES.Data.Repositories
{
    public class RfidRepository : BaseRepository <tblRfid>
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public RfidRepository(XHTD_Entities appDbContext) : base(appDbContext)
        {
        }

        public bool CheckValidCode(string code)
        {
            bool isValid = false;
            if (code.StartsWith("9") || code.StartsWith("22"))
            {
                using (var dbContext = new XHTD_Entities())
                {
                    isValid = dbContext.tblRfids.Any(x => x.Code == code);
                }
            }
            
            return isValid;
        }
    }
}
