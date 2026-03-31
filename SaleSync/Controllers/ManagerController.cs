using Microsoft.AspNetCore.Mvc;
using SaleSync.Models;
using System.Collections.Generic;

namespace SaleSync.Controllers
{
    public class ManagerController : Controller
    {
        private static List<InventoryItems> inventoryItems = new List<InventoryItems>();

        public IActionResult Dashboard()
        {
            return View("ManagerDashboard");
        }

        public IActionResult Inventory()
        {
            // REUSE Admin Inventory page
            return View("~/Views/Admin/Inventory.cshtml", inventoryItems);
        }
    }
}