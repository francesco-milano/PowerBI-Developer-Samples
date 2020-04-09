using Microsoft.PowerBI.Api.Models;
using System;

namespace PowerBIEmbedded_AppOwnsData.Models
{
    public class ExportConfig
    {
        public string FileName { get; set; }
        public ExportedFile File { get; set; }
        public string ErrorMessage { get; set; }
    }
}
