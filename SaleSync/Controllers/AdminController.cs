using Microsoft.AspNetCore.Mvc;
using SaleSync.Models;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;

namespace SaleSync.Controllers
{
    public class AdminController : Controller
    {
        private static List<InventoryItems> inventoryItems = new List<InventoryItems>();

        public IActionResult Dashboard()
        {
            var role = HttpContext.Session.GetString("Role");

            if (string.IsNullOrEmpty(role) || role != "Admin")
                return RedirectToAction("Index", "Home");

            return View("AdminDashboard");
        }

        [HttpGet]
        public IActionResult Inventory()
        {
            var role = HttpContext.Session.GetString("Role");

            if (role != "Admin")
                return RedirectToAction("Index", "Home");

            return View(inventoryItems);
        }

        [HttpPost]
        public IActionResult AddInventory(InventoryItems item)
        {
            var role = HttpContext.Session.GetString("Role");

            if (role != "Admin")
                return RedirectToAction("Index", "Home");

            if (item != null)
            {
                inventoryItems.Add(item);
            }

            return RedirectToAction("Inventory");
        }

        public IActionResult ManageAccounts()
        {
            var role = HttpContext.Session.GetString("Role");

            if (role != "Admin")
                return RedirectToAction("Index", "Home");

            return View();
        }
    }
}