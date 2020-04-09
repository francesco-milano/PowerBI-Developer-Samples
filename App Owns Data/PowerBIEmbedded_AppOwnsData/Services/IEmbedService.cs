using PowerBIEmbedded_AppOwnsData.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Mvc;

namespace PowerBIEmbedded_AppOwnsData.Models
{
    public interface IEmbedService
    {
        ExportConfig ExportConfig { get; }
        EmbedConfig EmbedConfig { get; }
        TileEmbedConfig TileEmbedConfig { get; }

        Task<bool> ExportReport();
        Task<bool> EmbedReport(string userName, string roles);
        Task<bool> EmbedDashboard();
        Task<bool> EmbedTile();
    }
}
