using Microsoft.AspNetCore.Mvc;
using SaleSync.Models;
using System.Collections.Generic;

namespace SaleSync.Controllers
{
    public class AdminController : Controller
    {
        private static List<InventoryItems> inventoryItems = new List<InventoryItems>();

        public IActionResult Dashboard()
        {
            var userName = HttpContext.Session.GetString("UserName");

            if (string.IsNullOrEmpty(userName))
            {
                return RedirectToAction("Index", "Home");
            }

            return View("AdminDashboard");
        }

        [HttpGet]
        public IActionResult Inventory()
        {
            return View(inventoryItems);
        }

        [HttpPost]
        public IActionResult AddInventory(InventoryItems item)
        {
            if (item != null)
            {
                inventoryItems.Add(item);
            }

            return RedirectToAction("Inventory");
        }

        public IActionResult ManageAccounts()
        {
            return View();
        }
    }
}