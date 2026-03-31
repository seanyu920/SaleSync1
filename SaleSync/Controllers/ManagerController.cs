using Microsoft.AspNetCore.Mvc;
using SaleSync.Models;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;

namespace SaleSync.Controllers
{
    public class ManagerController : Controller
    {
        private static List<InventoryItems> inventoryItems = new List<InventoryItems>();

        public IActionResult Dashboard()
        {
            var role = HttpContext.Session.GetString("Role");

            if (string.IsNullOrEmpty(role) || role != "Manager")
                return RedirectToAction("Index", "Home");

            return View("ManagerDashboard");
        }

        public IActionResult Inventory()
        {
            // REUSE Admin Inventory page
            return View("~/Views/Admin/Inventory.cshtml", inventoryItems);
        }
    }
}