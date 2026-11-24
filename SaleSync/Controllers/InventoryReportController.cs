using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaleSync.Models;
using SaleSync.Services;

namespace SaleSync.Controllers
{
    /// <summary>
    /// The Inventory Stock Report: everything that went into and out of the stock room,
    /// with the details around it (who, when, why, how much it was worth, what needs attention).
    /// </summary>
    [Authorize(Roles = "Admin,Manager")]
    public class InventoryReportController : Controller
    {
        private static readonly string[] AllowedTypes = { "all", "in", "out", "restock", "initial", "sale", "adjustment", "edit" };

        private readonly InventoryMovementService _movements;
        private readonly StoreSettingsService _storeSettings;

        public InventoryReportController(InventoryMovementService movements, StoreSettingsService storeSettings)
        {
            _movements = movements;
            _storeSettings = storeSettings;
        }

        [HttpGet]
        public IActionResult Index(string? range, [FromQuery(Name = "from")] DateTime? fromDate, [FromQuery(Name = "to")] DateTime? toDate, string? type, int? productId)
        {
            var filter = BuildFilter(range, fromDate, toDate, type, productId, out string normalizedRange);
            filter.MaxRows = 1000;

            var settings = _storeSettings.GetSettings();
            var model = _movements.BuildReport(normalizedRange, filter, settings.LowStockThreshold);

            return View(model);
        }

        [HttpGet]
        public IActionResult ExportCsv(string? range, [FromQuery(Name = "from")] DateTime? fromDate, [FromQuery(Name = "to")] DateTime? toDate, string? type, int? productId)
        {
            var filter = BuildFilter(range, fromDate, toDate, type, productId, out _);

            var rows = _movements.GetMovementsForExport(filter)
                .OrderBy(r => r.Date)
                .ThenBy(r => r.MovementId)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("Date,Item,Movement,Direction,Quantity Change,Unit,Quantity Before,Quantity After,Unit Cost,Value,Supplier,Reference,Done By,Notes");

            foreach (var r in rows)
            {
                sb.Append(Csv(r.Date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))).Append(',');
                sb.Append(Csv(r.ItemName)).Append(',');
                sb.Append(Csv(r.MovementType)).Append(',');
                sb.Append(r.IsEdit ? "Edit" : (r.IsIn ? "In" : "Out")).Append(',');
                sb.Append(Num(r.QuantityChange)).Append(',');
                sb.Append(Csv(r.Unit)).Append(',');
                sb.Append(Num(r.QuantityBefore)).Append(',');
                sb.Append(Num(r.QuantityAfter)).Append(',');
                sb.Append(Num(r.UnitCost)).Append(',');
                sb.Append(Num(r.TotalValue)).Append(',');
                sb.Append(Csv(r.Supplier)).Append(',');
                sb.Append(Csv(r.ReferenceLabel)).Append(',');
                sb.Append(Csv(r.PerformedBy)).Append(',');
                sb.Append(Csv(r.Notes));
                sb.AppendLine();
            }

            // UTF-8 with a byte order mark so Excel opens it correctly
            var bytes = Encoding.UTF8.GetPreamble()
                .Concat(Encoding.UTF8.GetBytes(sb.ToString()))
                .ToArray();

            string fileName = $"inventory-movements_{filter.From:yyyy-MM-dd}_to_{filter.To:yyyy-MM-dd}.csv";
            return File(bytes, "text/csv", fileName);
        }

        // ---------------------------------------------------------------------------------

        private static InventoryReportFilter BuildFilter(string? range, DateTime? fromDate, DateTime? toDate,
                                                         string? type, int? productId, out string normalizedRange)
        {
            DateTime today = DateTime.Today;
            DateTime start;
            DateTime end;

            switch ((range ?? "30d").Trim().ToLowerInvariant())
            {
                case "today":
                    normalizedRange = "today";
                    start = today;
                    end = today;
                    break;

                case "7d":
                    normalizedRange = "7d";
                    start = today.AddDays(-6);
                    end = today;
                    break;

                case "month":
                    normalizedRange = "month";
                    start = new DateTime(today.Year, today.Month, 1);
                    end = today;
                    break;

                case "custom":
                    normalizedRange = "custom";
                    start = (fromDate ?? today.AddDays(-29)).Date;
                    end = (toDate ?? today).Date;
                    if (end < start)
                    {
                        (start, end) = (end, start);
                    }
                    break;

                default:
                    normalizedRange = "30d";
                    start = today.AddDays(-29);
                    end = today;
                    break;
            }

            string cleanType = (type ?? "all").Trim().ToLowerInvariant();
            if (!AllowedTypes.Contains(cleanType)) cleanType = "all";

            return new InventoryReportFilter
            {
                From = start,
                To = end,
                Type = cleanType,
                ProductId = productId.HasValue && productId.Value > 0 ? productId : null
            };
        }

        private static string Num(decimal? value)
        {
            return value.HasValue ? value.Value.ToString("0.####", CultureInfo.InvariantCulture) : "";
        }

        /// <summary>Quotes a text value for CSV and defuses spreadsheet formulas.</summary>
        private static string Csv(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";

            if ("=+-@\t\r".IndexOf(value[0]) >= 0) value = "'" + value;

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
