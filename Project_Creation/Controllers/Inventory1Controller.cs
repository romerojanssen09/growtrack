using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Project_Creation.Data;
using Project_Creation.DTO;
using Project_Creation.Models.Entities;
using Project_Creation.Models.ViewModels;
using System.Security.Claims;
using Project_Creation.Models;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using static Project_Creation.Models.Entities.Users;
using Microsoft.AspNetCore.SignalR;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Project_Creation.Controllers
{
    public class Inventory1Controller : Controller
    {
        private readonly AuthDbContext _context;
        private readonly IWebHostEnvironment _hostEnvironment;
        private readonly ILogger<Inventory1Controller> _logger;
        private readonly IHubContext<RealTimeHub> _hubContext;

        public Inventory1Controller(
            AuthDbContext context,
            IWebHostEnvironment hostEnvironment,
            ILogger<Inventory1Controller> logger,
            IHubContext<RealTimeHub> hubContext)
        {
            _context = context;
            _hostEnvironment = hostEnvironment;
            _logger = logger;
            _hubContext = hubContext;
        }

        // GET: Inventory1
        public async Task<IActionResult> Index()
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var currentUserRole = User.FindFirstValue(ClaimTypes.Role);
            int boId = int.TryParse(User.FindFirstValue("BOId"), out var tempBoId) ? tempBoId : 0;
            int who = currentUserRole == "Staff" ? boId : GetCurrentUserId();
            var products = await _context.Products2
                .Where(p => p.BOId == who)
                .ToListAsync();

            // Get all suppliers for the current user
            var suppliers = await _context.Supplier2
                .Where(s => s.BOId == who)
                .ToListAsync();

            var isAllowed = await _context.Users
                .AnyAsync(u => u.MarkerPlaceStatus == MarketplaceStatus.Authorized);

            ViewBag.Suppliers = suppliers;
            ViewBag.IsAllowedMarketplace = isAllowed;

            return View(products);
        }

        [HttpPost]
        public async Task<IActionResult> DeleteCategory(int id)
        {
            var category = await _context.Categories.FindAsync(id);
            if (category == null)
            {
                return Json(new { success = false });
            }
            _context.Categories.Remove(category);
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }


        [HttpGet]
        public async Task<IActionResult> Create()
        {
            var productDto = new ProductDto
            {
                BOId = GetCurrentUserId(),
                ProductName = "",
                PurchasePrice = 0,
                SKU = "AUTO-GENERATED",
                Barcode = "",
                QuantityInStock = 0,
                ReorderLevel = 10,
                SellingPrice = 0,
                Suppliers = await _context.Supplier2
                    .Where(s => s.BOId == GetCurrentUserId())
                    .ToListAsync(),
                Category = await _context.Categories
                    .Where(c => c.BOId == GetCurrentUserId())
                    .ToListAsync()
            };

            return View(productDto);
        }

        private int GetCurrentUserId()
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            {
                _logger.LogWarning("Invalid or missing user ID claim");
                throw new InvalidOperationException("User is not authenticated");
            }

            var currentUserRole = User.FindFirstValue(ClaimTypes.Role);
            int boId = int.TryParse(User.FindFirstValue("BOId"), out var tempBoId) ? tempBoId : 0;
            int who = currentUserRole == "Staff" ? boId : userId;
            return who;
        }

        private string GenerateSKU(string productName, string categoryName)
        {
            var namePart = productName?.Length >= 3
                ? productName.Substring(0, 3).ToUpper()
                : "PRD";

            var catPart = categoryName?.Length >= 3
                ? categoryName.Substring(0, 3).ToUpper()
                : "CAT";

            return $"{namePart}-{catPart}-{TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Singapore")):MMddHHmm}";
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(ProductDto productDto)
        {
            try
            {
                string effectiveCategory = null;

                if (!string.IsNullOrWhiteSpace(productDto.NewCategoryName))
                {
                    effectiveCategory = productDto.NewCategoryName;
                    _logger.LogDebug("Using new category: {CategoryName}", effectiveCategory);
                }
                else if (!string.IsNullOrEmpty(productDto.Category2))
                {
                    effectiveCategory = productDto.Category2;
                    _logger.LogDebug("Using existing category: {CategoryName}", effectiveCategory);
                }

                if (string.IsNullOrEmpty(effectiveCategory))
                {
                    ModelState.AddModelError("Category", "Please select or create a category");
                }

                if (!ModelState.IsValid)
                {
                    await PopulateDropdowns(productDto);
                    return View(productDto);
                }

                var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
                var currentUserRole = User.FindFirstValue(ClaimTypes.Role);
                int boId = int.TryParse(User.FindFirstValue("BOId"), out var tempBoId) ? tempBoId : 0;

                var product = new Product
                {
                    BOId = currentUserRole == "Staff" ? boId : GetCurrentUserId(),
                    ProductName = productDto.ProductName,
                    SupplierId = productDto.SupplierId?.ToString(),
                    Category = effectiveCategory ?? "categories not found",
                    PurchasePrice = productDto.PurchasePrice,
                    SKU = productDto.SKU,
                    QuantityInStock = productDto.QuantityInStock,
                    ReorderLevel = productDto.ReorderLevel,
                    Description = productDto.Description,
                    SellingPrice = productDto.SellingPrice,
                    CreatedAt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Singapore"))
                };

                // Use the execution strategy instead of direct transaction
                var strategy = _context.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    using var transaction = await _context.Database.BeginTransactionAsync();
                    try
                    {
                        // Add category if new
                        if (!string.IsNullOrEmpty(productDto.NewCategoryName))
                        {
                            bool categoryExists = await _context.Categories.AnyAsync(c =>
                                c.CategoryName.ToLower() == productDto.NewCategoryName.ToLower()
                                && c.BOId == GetCurrentUserId());

                            if (!categoryExists)
                            {
                                var newCategory = new Category
                                {
                                    CategoryName = productDto.NewCategoryName,
                                    BOId = GetCurrentUserId()
                                };
                                _context.Categories.Add(newCategory);
                                await _context.SaveChangesAsync();
                            }
                        }

                        _context.Products2.Add(product);
                        await _context.SaveChangesAsync();

                        var log = new InventoryLog
                        {
                            BOId = GetCurrentUserId(),
                            ProductId = product.Id,
                            ProductName = product.ProductName,
                            QuantityBefore = 0,
                            QuantityAfter = product.QuantityInStock,
                            MovementType = InventoryMovementTypes.NewProduct,
                            ReferenceId = $"NEWPROD-{product.Id}",
                            Notes = $"New product created with initial stock: {product.QuantityInStock}",
                            Timestamp = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Singapore"))
                        };
                        _context.InventoryLogs.Add(log);
                        await _context.SaveChangesAsync();

                        // Save barcode if provided
                        if (!string.IsNullOrEmpty(productDto.Barcode))
                        {
                            product.Barcode = await SaveBarcodeImage(productDto.Barcode, productDto.SKU);
                            _context.Products2.Update(product);
                            await _context.SaveChangesAsync();
                        }

                        await transaction.CommitAsync();
                    }
                    catch (Exception)
                    {
                        await transaction.RollbackAsync();
                        throw;
                    }
                });

                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during product creation");
                ModelState.AddModelError("", "Error saving product. Please try again.");
                await PopulateDropdowns(productDto);
                return View(productDto);
            }
        }

        private async Task PopulateDropdowns(ProductDto productDto)
        {
            productDto.Suppliers = await _context.Supplier2
                .Where(s => s.BOId == GetCurrentUserId()).ToListAsync();
            productDto.Category = await _context.Categories
                .Where(c => c.BOId == GetCurrentUserId()).ToListAsync();
        }


        // Keep the SaveBarcodeImage method as is
        private async Task<string> SaveBarcodeImage(string imageData, string sku)
        {
            try
            {
                var base64Data = imageData.Split(',')[1];
                var imageBytes = Convert.FromBase64String(base64Data);

                string userId = GetCurrentUserId().ToString();
                string uploadsFolder = Path.Combine(_hostEnvironment.WebRootPath, "uploads", userId, "barcodes");

                if (!Directory.Exists(uploadsFolder))
                {
                    Directory.CreateDirectory(uploadsFolder);
                }

                string fileName = $"barcode_{sku}_{TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Singapore")):yyyyMMddHHmmss}.png";
                string filePath = Path.Combine(uploadsFolder, fileName);

                await System.IO.File.WriteAllBytesAsync(filePath, imageBytes);

                return $"/uploads/{userId}/barcodes/{fileName}";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving barcode image");
                return string.Empty;
            }
        }

        // GET: Inventory1/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var product = await _context.Products2
                .FirstOrDefaultAsync(m => m.Id == id);

            if (product == null)
            {
                return NotFound();
            }

            // Get all suppliers for the current user
            var suppliers = await _context.Supplier2
                .Where(s => s.BOId == GetCurrentUserId())
                .ToListAsync();

            ViewBag.Suppliers = suppliers;

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                return PartialView("_DetailsPartial", product);
            }

            return View(product);
        }

        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();

            var product = await _context.Products2
                .Include(p => p.Supplier2)  // Include the supplier
                .FirstOrDefaultAsync(p => p.Id == id);

            if (product == null) return NotFound();

            var productDto = new ProductDto
            {
                Id = product.Id,
                ProductName = product.ProductName,
                SupplierId = int.TryParse(product.SupplierId, out var supplierId) ? supplierId : null,
                Supplier2 = product.Supplier2,  // Set the supplier navigation property
                PurchasePrice = product.PurchasePrice,
                SKU = product.SKU,
                Barcode = product.Barcode,
                QuantityInStock = product.QuantityInStock,
                ReorderLevel = product.ReorderLevel,
                Description = product.Description,
                SellingPrice = product.SellingPrice,
                Suppliers = await _context.Supplier2.Where(u => u.BOId == GetCurrentUserId()).ToListAsync(),
                Category = await _context.Categories.Where(u => u.BOId == GetCurrentUserId()).ToListAsync(),
                NewCategoryName = product.Category
            };

            return View(productDto);
        }

        // POST: Inventory1/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, ProductDto productDto)
        {
            if (id != productDto.Id)
            {
                return NotFound();
            }

            if (!ModelState.IsValid)
            {
                productDto.Suppliers = await _context.Supplier2.ToListAsync();
                productDto.Category = await _context.Categories.ToListAsync();
                return View(productDto);
            }

            try
            {
                var product = await _context.Products2.FindAsync(id);
                if (product == null)
                {
                    return NotFound();
                }

                product.ProductName = productDto.ProductName;
                product.SupplierId = productDto.SupplierId?.ToString();
                product.PurchasePrice = productDto.PurchasePrice;
                product.SKU = productDto.SKU;
                product.QuantityInStock = productDto.QuantityInStock;
                product.ReorderLevel = productDto.ReorderLevel;
                product.Description = productDto.Description;
                product.SellingPrice = productDto.SellingPrice;

                // Determine and update the effective category
                if (!string.IsNullOrWhiteSpace(productDto.NewCategoryName))
                {
                    // Check if category already exists
                    bool categoryExists = await _context.Categories
                        .AnyAsync(c => c.CategoryName.ToLower() == productDto.NewCategoryName.ToLower()
                                       && c.BOId == GetCurrentUserId());

                    if (!categoryExists)
                    {
                        var newCategory = new Category
                        {
                            CategoryName = productDto.NewCategoryName,
                            BOId = GetCurrentUserId()
                        };
                        _context.Categories.Add(newCategory);
                        await _context.SaveChangesAsync();
                    }

                    product.Category = productDto.NewCategoryName;
                }
                else if (!string.IsNullOrEmpty(productDto.Category2))
                {
                    product.Category = productDto.Category2;
                }

                // Handle new barcode image if provided
                if (!string.IsNullOrEmpty(productDto.Barcode) && productDto.Barcode.StartsWith("data:image"))
                {
                    product.Barcode = await SaveBarcodeImage(productDto.Barcode, productDto.SKU);
                }

                await _context.SaveChangesAsync();
                _logger.LogInformation("Product updated successfully: {ProductId}", product.Id);

                return RedirectToAction(nameof(Index));
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!ProductExists(productDto.Id))
                {
                    return NotFound();
                }
                else
                {
                    throw;
                }
            }
        }

        // Add this helper method to the controller:
        private string GetProductChanges(Product original, Product updated)
        {
            var changes = new List<string>();

            if (original.ProductName != updated.ProductName)
                changes.Add($"Name: {original.ProductName} → {updated.ProductName}");

            if (original.QuantityInStock != updated.QuantityInStock)
                changes.Add($"Stock: {original.QuantityInStock} → {updated.QuantityInStock}");

            if (original.SellingPrice != updated.SellingPrice)
                changes.Add($"Price: {original.SellingPrice} → {updated.SellingPrice}");

            return changes.Any() ? string.Join(", ", changes) : "No quantity changes";
        }

        private bool ProductExists(int id)
        {
            return _context.Products2.Any(e => e.Id == id);
        }

        // POST: Inventory1/Delete/5
        [HttpPost]
        public async Task<IActionResult> Delete(int productId)
        {
            var product = await _context.Products2.FindAsync(productId);
            if (product == null)
            {
                return Json(new { success = false, message = "Product not found." });
            }

            try
            {
                // In the Delete method, before removing the product, add:
                var log = new InventoryLog
                {
                    BOId = GetCurrentUserId(),
                    ProductId = product.Id,
                    ProductName = product.ProductName,
                    QuantityBefore = product.QuantityInStock,
                    QuantityAfter = 0,
                    MovementType = InventoryMovementTypes.DeleteProduct,
                    ReferenceId = $"DELETE-{product.Id}",
                    Notes = $"Product deleted. Final stock was {product.QuantityInStock}",
                    Timestamp = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Singapore"))
                };
                _context.InventoryLogs.Add(log);
                _context.Products2.Remove(product);
                await _context.SaveChangesAsync();
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // GET: Inventory1/QuickSale
        [HttpGet]
        public async Task<IActionResult> QuickSale()
        {
            try
            {
                var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
                var currentUserRole = User.FindFirstValue(ClaimTypes.Role);
                int boId = int.TryParse(User.FindFirstValue("BOId"), out var tempBoId) ? tempBoId : 0;
                int who = currentUserRole == "Staff" ? boId : GetCurrentUserId();
                _logger.LogInformation("Loading products for QuickSale");
                var products = await _context.Products2
                    .Where(p => p.BOId == who && p.QuantityInStock > 0)
                    .OrderBy(p => p.ProductName)
                    .ToListAsync();

                // Set products in ViewBag
                ViewBag.Products = products;

                _logger.LogInformation("Loaded {ProductCount} products for QuickSale", products.Count);
                
                return View(new QuickSaleViewModel
                {
                    AvailableProducts = products,
                    SaleDate = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Singapore")),
                    SelectedOptions = "new", // Default to new customer option
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading QuickSale page");
                return View("Error", new ErrorViewModel
                {
                    RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
                });
            }
        }

        private async Task<int> CalculatePointsForProduct(int leadId, int productId)
        {
            DateTime thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);

            // Get the lead's purchase history in last 30 days
            var recentPurchases = await _context.Sales
                .Where(s => s.LeadId == leadId && s.SaleDate >= thirtyDaysAgo)
                .ToListAsync();

            // If no recent purchases, this is effectively a new customer
            if (!recentPurchases.Any())
            {
                return 1; // Default points for a new purchase
            }

            // Check if this specific product was purchased in the last 30 days
            var lastPurchasedProducts = await _context.Sales
                .Where(s => s.LeadId == leadId && s.SaleDate >= thirtyDaysAgo && s.ProductId == productId)
                .AnyAsync();

            // Award 2 points for different product, 1 point for same product
            return lastPurchasedProducts ? 1 : 2;
        }

        // Updated method to handle adding points from multiple products
        private async Task<Dictionary<int, int>> CalculatePointsPerProduct(int leadId, List<SaleItemViewModel> items)
        {
            var pointsPerProduct = new Dictionary<int, int>();
            
            // Get the list of product IDs this lead has purchased in the last 30 days
            DateTime thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);
            var recentlyPurchasedProductIds = await _context.Sales
                .Where(s => s.LeadId == leadId && s.SaleDate >= thirtyDaysAgo)
                .Select(s => s.ProductId)
                .Distinct()
                .ToListAsync();

            // Store which products are already in the current transaction to avoid double-counting
            var productsInCurrentTransaction = new HashSet<int>();
            
            // Calculate points for each product in the current purchase
            foreach (var item in items)
            {
                // If this product is already in the current transaction, assign 0 points
                if (productsInCurrentTransaction.Contains(item.ProductId))
                {
                    pointsPerProduct[item.ProductId] = 0;
                    _logger.LogInformation("Product {ProductId} appears multiple times in this transaction. Assigning 0 points for duplicates.", item.ProductId);
                    continue;
                }
                
                // Add to tracked products for this transaction
                productsInCurrentTransaction.Add(item.ProductId);
                
                // Check if this product was purchased before
                bool isPreviouslyPurchased = recentlyPurchasedProductIds.Contains(item.ProductId);
                
                // Assign points - 2 for new product, 1 for previously purchased
                int productPoints = isPreviouslyPurchased ? 1 : 2;
                pointsPerProduct[item.ProductId] = productPoints;
                
                _logger.LogInformation("Calculated {Points} points for product {ProductId} (previously purchased: {PreviouslyPurchased})",
                    productPoints, item.ProductId, isPreviouslyPurchased);
            }
            
            return pointsPerProduct;
        }

        private async Task UpdateLeadPoints(int leadId, List<SaleItemViewModel> items)
        {
            var lead = await _context.Leads.FindAsync(leadId);
            if (lead == null) return;

            // Start with current points or 0
            int totalPoints = lead.LeadPoints ?? 0;
            decimal totalSpend = 0;
            
            // Calculate points per product - handling duplicates correctly
            var pointsPerProduct = await CalculatePointsPerProduct(leadId, items);
            
            // Calculate total spend and add product-specific points
            foreach (var item in items)
            {
                // Add to total spend
                decimal itemSpend = item.Price * item.Quantity;
                totalSpend += itemSpend;
                
                // Get points for this product (will be 0 for duplicates in same transaction)
                if (pointsPerProduct.TryGetValue(item.ProductId, out int productPoints))
                {
                    totalPoints += productPoints;
                }
            }
            
            // Add points based on total spend (1 point per 1000 spent)
            int spendPoints = (int)(totalSpend / 1000);
            if (spendPoints > 0)
            {
                totalPoints += spendPoints;
                _logger.LogInformation("Adding {Points} points for total spend of {TotalSpend} to lead {LeadId}", 
                    spendPoints, totalSpend, leadId);
            }
            
            // Update the lead with total points
            lead.LeadPoints = totalPoints;
            lead.LastPurchaseDate = GetSingaporeTime();
            
            // Set the last purchased product ID to the last product in the list
            if (items.Any())
            {
                lead.LastPurchasedId = items.Last().ProductId;
            }
            
            _context.Leads.Update(lead);
            await _context.SaveChangesAsync();
            
            _logger.LogInformation("Updated lead {LeadId} with total points: {Points}", leadId, totalPoints);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> QuickSale(QuickSaleViewModel model)
        {
            if(model == null)
            {
                _logger.LogError("QuickSale model is null");
                return BadRequest("Invalid sale data");
            }

            // Log the selected option value to diagnose the issue
            _logger.LogInformation("SelectedOptions value: {SelectedOptions}", model.SelectedOptions ?? "null");

            // Check if a customer type is selected
            if (string.IsNullOrEmpty(model.SelectedOptions))
            {
                _logger.LogWarning("No customer type (SelectedOptions) selected");
                ModelState.AddModelError("SelectedOptions", "Please select a customer type (New Customer or Anonymous Buyer)");
                
                // Repopulate the model for the view
                var products = await _context.Products2
                    .Where(p => p.BOId == GetCurrentUserId() && p.QuantityInStock > 0)
                    .OrderBy(p => p.ProductName)
                    .ToListAsync();

                ViewBag.Products = products;
                model.AvailableProducts = products;
                
                return View(model);
            }

            // Validate that there are items in the sale
            if (model.Items == null || !model.Items.Any())
            {
                _logger.LogWarning("No items in the sale. Adding ModelState error.");
                ModelState.AddModelError("", "At least one item is required for the sale");
                
                // Repopulate the model for the view
                var products = await _context.Products2
                    .Where(p => p.BOId == GetCurrentUserId() && p.QuantityInStock > 0)
                    .OrderBy(p => p.ProductName)
                    .ToListAsync();

                ViewBag.Products = products;
                model.AvailableProducts = products;
                
                return View(model);
            }

            if (model.SelectedOptions == "new")
            {
                // Check required fields for new lead
                if (string.IsNullOrWhiteSpace(model.CustomerName))
                {
                    _logger.LogWarning("Customer name is required for new lead");
                    ModelState.AddModelError("CustomerName", "Customer name is required for new lead");
                    
                    // Repopulate the model for the view
                    var products = await _context.Products2
                        .Where(p => p.BOId == GetCurrentUserId() && p.QuantityInStock > 0)
                        .OrderBy(p => p.ProductName)
                        .ToListAsync();

                    ViewBag.Products = products;
                    model.AvailableProducts = products;
                    
                    return View(model);
                }
                
                try
                {
                    // First, check if this might be an existing lead
                    var existingLead = await FindExistingLead(model.CustomerName, model.CustomerEmail, model.CustomerPhone);
                    
                    if (existingLead != null)
                    {
                        _logger.LogInformation("Found existing lead that matches the new customer: {LeadId}, {LeadName}", 
                            existingLead.Id, existingLead.LeadName);
                        
                        // Update the existing lead with any new information
                        bool leadUpdated = false;
                        
                        // Update email if it was empty and we now have one
                        if (string.IsNullOrWhiteSpace(existingLead.LeadEmail) && !string.IsNullOrWhiteSpace(model.CustomerEmail))
                        {
                            existingLead.LeadEmail = model.CustomerEmail;
                            leadUpdated = true;
                        }
                        
                        // Update phone if it was empty and we now have one
                        if (string.IsNullOrWhiteSpace(existingLead.LeadPhone) && !string.IsNullOrWhiteSpace(model.CustomerPhone))
                        {
                            existingLead.LeadPhone = model.CustomerPhone;
                            leadUpdated = true;
                        }
                        
                        if (leadUpdated)
                        {
                            existingLead.UpdatedAt = GetSingaporeTime();
                            _context.Leads.Update(existingLead);
                            await _context.SaveChangesAsync();
                            _logger.LogInformation("Updated existing lead with new information: {LeadId}", existingLead.Id);
                        }
                        
                        // Use the existing lead for this sale
                        model.LeadId = existingLead.Id;
                        model.CustomerName = existingLead.LeadName;
                        
                        // Update lead points
                        await UpdateLeadPoints(existingLead.Id, model.Items);
                        
                        // Set TempData to inform the user that we found an existing lead
                        TempData["LeadMatched"] = $"Found existing lead '{existingLead.LeadName}' that matches the customer information. The sale has been associated with this lead.";
                    }
                    else
                    {
                        // Create a new lead since no match was found
                        var newLead = new Leads
                        {
                            LeadName = model.CustomerName,
                            LeadEmail = model.CustomerEmail ?? "",
                            LeadPhone = model.CustomerPhone ?? "",
                            Notes = "Created from QuickSale",
                            CreatedAt = GetSingaporeTime(),
                            CreatedById = GetCurrentUserId(),
                            LeadPoints = 0, // Initialize points for new lead
                            LastPurchaseDate = GetSingaporeTime(),
                            IsAllowToCampaign = model.IsAllowToCampaign
                        };

                        if (model.Items.Any())
                        {
                            newLead.LastPurchasedId = model.Items.First().ProductId;
                        }

                        _context.Leads.Add(newLead);
                        await _context.SaveChangesAsync(); // Save to get the ID
                        
                        _logger.LogInformation("Created new lead: {LeadName} with ID: {LeadId}", newLead.LeadName, newLead.Id);
                        
                        // Set the LeadId for the sales record
                        model.LeadId = newLead.Id;
                        
                        // Update lead data for each item
                        await UpdateLeadPoints(newLead.Id, model.Items);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error creating or finding lead: {Message}", ex.Message);
                    ModelState.AddModelError("", "Error creating or finding lead: " + ex.Message);
                    return View(model);
                }
            }

            // Handle anonymous buyer option
            if (model.SelectedOptions == "anonymous")
            {
                // For anonymous buyers, we don't create a lead or associate with an existing one
                // Just set the customer name for the sales record
                model.CustomerName = "Anonymous Buyer";
                model.LeadId = null; // Ensure no lead is associated
            }

            // Process each item in the sale
            Dictionary<int, int> productPoints = new Dictionary<int, int>();
            
            // Calculate points per product if we have a lead
            if (model.LeadId.HasValue)
            {
                productPoints = await CalculatePointsPerProduct(model.LeadId.Value, model.Items);
            }
            
            foreach (var item in model.Items)
            {
                try
                {
                    // Get the product
                    var product = await _context.Products2.FindAsync(item.ProductId);
                    if (product == null)
                    {
                        _logger.LogWarning("Product with ID {ProductId} not found", item.ProductId);
                        ModelState.AddModelError("", $"Product with ID {item.ProductId} not found");
                        continue;
                    }

                    // Check inventory
                    if (product.QuantityInStock < item.Quantity)
                    {
                        _logger.LogWarning("Not enough inventory for product {ProductName}. Requested: {Requested}, Available: {Available}",
                            product.ProductName, item.Quantity, product.QuantityInStock);
                        ModelState.AddModelError("", $"Not enough inventory for {product.ProductName}. Available: {product.QuantityInStock}");
                        continue;
                    }

                    // Update product stock
                    int oldQuantity = product.QuantityInStock;
                    product.QuantityInStock -= item.Quantity;
                    _context.Products2.Update(product);

                    // Get the points earned for this specific product
                    int pointsEarned = 0;
                    if (model.LeadId.HasValue && productPoints.TryGetValue(item.ProductId, out int points))
                    {
                        pointsEarned = points;
                    }

                    // Create sale record
                    var sale = new Sale
                    {
                        BOId = GetCurrentUserId(),
                        ProductId = item.ProductId,
                        CustomerName = model.CustomerName ?? "Walk-in Customer",
                        TotalAmount = item.Quantity * item.Price,
                        SaleDate = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Singapore")),
                        LeadId = model.LeadId,
                        CustomerEmail = model.CustomerEmail,
                        CustomerPhone = model.CustomerPhone,
                        EarnedPoints = pointsEarned // Store the points earned for this product
                    };
                    _context.Sales.Add(sale);
                    await _context.SaveChangesAsync(); // Save to get the Sale ID
                    
                    // Create a SaleItem record linked to this Sale
                    var saleItem = new SaleItem
                    {
                        SaleId = sale.Id,
                        ProductId = item.ProductId,
                        ProductName = product.ProductName,
                        Quantity = item.Quantity,
                        UnitPrice = item.Price,
                        TotalPrice = item.Quantity * item.Price,
                        Notes = $"QuickSale on {DateTime.Now.ToString("yyyy-MM-dd")}"
                    };
                    _context.SaleItems.Add(saleItem);

                    // Create inventory log
                    var log = new InventoryLog
                    {
                        BOId = GetCurrentUserId(),
                        ProductId = product.Id,
                        ProductName = product.ProductName,
                        QuantityBefore = oldQuantity,
                        QuantityAfter = product.QuantityInStock,
                        MovementType = InventoryMovementTypes.Sale,
                        ReferenceId = $"SALE-PENDING", // Will update after sale is saved
                        Notes = $"Sale to {model.CustomerName ?? "Customer"} (Item: {item.Quantity} x {product.ProductName})",
                        Timestamp = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Singapore"))
                    };
                    _context.InventoryLogs.Add(log);

                    await _context.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing sale item with ProductId={ProductId}", item.ProductId);
                    ModelState.AddModelError("", $"Error processing item {item.ProductId}: {ex.Message}");
                }
            }

            if (!ModelState.IsValid)
            {
                // If there were errors, reload the page with the model
                var products = await _context.Products2
                    .Where(p => p.BOId == GetCurrentUserId() && p.QuantityInStock > 0)
                    .OrderBy(p => p.ProductName)
                    .ToListAsync();

                ViewBag.Products = products;
                model.AvailableProducts = products;
                
                return View(model);
            }

            return RedirectToAction("QuickSale");
        }

        [HttpGet]
        public async Task<IActionResult> GetProductDetails(int id)
        {
            try
            {
                _logger.LogInformation("Fetching product details for {ProductId}", id);
                var product = await _context.Products2.FindAsync(id);
                if (product == null)
                {
                    _logger.LogWarning("Product {ProductId} not found", id);
                    return NotFound();
                }

                _logger.LogInformation("Returning details for product {ProductId}: Price={Price}, Stock={Stock}",
                    id, product.SellingPrice, product.QuantityInStock);

                return Json(new
                {
                    price = product.SellingPrice,
                    availableStock = product.QuantityInStock
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching product details for {ProductId}", id);
                return StatusCode(500, "Error fetching product details");
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetProductPrice(int id)
        {
            var product = await _context.Products2.FindAsync(id);
            if (product == null)
            {
                return NotFound();
            }
            return Json(product.SellingPrice);
        }

        // GET: Inventory1/StockIn
        [HttpGet]
        public async Task<IActionResult> StockIn(int? id)
        {
            try
            {
                _logger.LogInformation("Loading StockIn page for user {UserId}", GetCurrentUserId());

                var products = await _context.Products2
                    .Where(p => p.BOId == GetCurrentUserId())
                    .OrderBy(p => p.ProductName)
                    .ToListAsync();

                _logger.LogDebug("Loaded {ProductCount} products for StockIn", products.Count);

                return View(new StockInViewModel
                {
                    AvailableProducts = products,
                    ProductId = id ?? 0,
                    ReceivingDate = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Singapore")),
                    Notes = "Stock In"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading StockIn page");

                return View("Error", new ErrorViewModel
                {
                    RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
                });
            }
        }

        // POST: Inventory1/StockIn
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> StockIn(StockInViewModel model)
        {
            _logger.LogInformation("Starting StockIn process for product {ProductId} with quantity {Quantity}",
                model.ProductId, model.Quantity);

            if (!ModelState.IsValid)
            {
                _logger.LogWarning("ModelState invalid for StockIn. Errors: {Errors}",
                    string.Join(", ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)));

                try
                {
                    model.AvailableProducts = await _context.Products2
                        .Where(p => p.BOId == GetCurrentUserId())
                        .ToListAsync();
                    return View(model);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error repopulating products for invalid StockIn model");
                    throw; // or handle gracefully
                }
            }

            _logger.LogDebug("Starting database transaction for StockIn");
            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                // Update product quantity
                var product = await _context.Products2.FindAsync(model.ProductId);
                if (product == null)
                {
                    _logger.LogError("Product {ProductId} not found during StockIn", model.ProductId);
                    ModelState.AddModelError("", "Product not found");
                    model.AvailableProducts = await _context.Products2
                        .Where(p => p.BOId == GetCurrentUserId())
                        .ToListAsync();
                    return View(model);
                }

                _logger.LogDebug("Current stock for product {ProductId}: {CurrentStock}. Adding {Quantity}",
                    model.ProductId, product.QuantityInStock, model.Quantity);

                product.QuantityInStock += model.Quantity;
                _context.Products2.Update(product);

                // Create inventory log
                var userId = GetCurrentUserId();
                // In InventoryController's StockIn method, update the log:
                var log = new InventoryLog
                {
                    BOId = userId,
                    ProductId = product.Id,
                    ProductName = product.ProductName,
                    QuantityBefore = product.QuantityInStock - model.Quantity,
                    QuantityAfter = product.QuantityInStock,
                    MovementType = InventoryMovementTypes.StockIn,
                    ReferenceId = $"STOCKIN-{TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Singapore")):yyyyMMddHHmmss}",
                    Notes = model.Notes ?? $"Stock In of {model.Quantity} items",
                    Timestamp = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Singapore"))
                };

                _context.InventoryLogs.Add(log);
                _logger.LogDebug("Created inventory log entry for StockIn");

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                _logger.LogInformation("Successfully processed StockIn for product {ProductId}. New quantity: {NewQuantity}",
                    model.ProductId, product.QuantityInStock);

                return RedirectToAction("Index", "Inventory1");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Error processing StockIn for product {ProductId}", model.ProductId);

                ModelState.AddModelError("", $"Error processing stock in: {ex.Message}");
                model.AvailableProducts = await _context.Products2
                    .Where(p => p.BOId == GetCurrentUserId())
                    .ToListAsync();
                return View(model);
            }
        }

        public static class InventoryMovementTypes
        {
            public const string Sale = "Sale";
            public const string Purchase = "Purchase";
            public const string Adjustment = "Adjustment";
            public const string StockIn = "Stock In";
            public const string NewProduct = "New Product";
            public const string EditProduct = "Edit Product";
            public const string DeleteProduct = "Delete Product";
        }

        [HttpGet]
        public async Task<IActionResult> PublishToMarketplace(int id)
        {
            var product = await _context.Products2
                .Include(p => p.Images)
                .FirstOrDefaultAsync(p => p.Id == id);

            if (product == null)
            {
                TempData["ErrorMessage"] = "Product not found";
                return RedirectToAction("Index");
            }
            
            var viewModel = new PublishToMarketplaceViewModel
            {
                ProductId = id,
                ProductName = product.ProductName,
                Description = product.Description,
                Price = product.SellingPrice,
                IsPublished = product.IsPublished,
                MarketplaceDescription = product.Description,
                MarketplacePrice = product.SellingPrice,
                DisplayFeatured = product.DisplayFeatured,
                ExistingImages = product.Images.Select(img => new ProductImageViewModel
                {
                    Id = img.Id,
                    ImagePath = img.ImagePath,
                    IsMainImage = img.IsMainImage,
                    Title = img.Title ?? product.ProductName,
                    DisplayOrder = img.DisplayOrder
                }).ToList()
            };
            
            return View(viewModel);
        }

        [HttpGet]
        public async Task<IActionResult> UnPublishToMarketplace(int id)
        {
            var product = await _context.Products2.FindAsync(id);
            if (product == null)
            {
                _logger.LogError("Product {id} not found in database", id);
                TempData["ErrorMessage"] = "Product not found.";
                return RedirectToAction("Index"); // or wherever your list is
            }

            product.IsPublished = false;
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = "Product has been unpublished successfully.";
            return RedirectToAction("Index"); // or redirect back to the list or detail page
        }

        [HttpGet]
        public async Task<IActionResult> PublishToMarketplaceAgain(int id)
        {
            var product = await _context.Products2.FindAsync(id);
            if (product == null)
            {
                _logger.LogError("Product {id} not found in database", id);
                TempData["ErrorMessage"] = "Product not found.";
                return RedirectToAction("Index"); // or wherever your list is
            }

            product.IsPublished = true;
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = "Product has been published successfully.";
            return RedirectToAction("Index"); // or redirect back to the list or detail page
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PublishToMarketplace(PublishToMarketplaceViewModel model)
        {
            _logger.LogInformation("Starting PublishToMarketplace for product {ProductId}", model.ProductId);

            if (!ModelState.IsValid)
            {
                _logger.LogWarning("ModelState invalid for product {ProductId}. Errors: {Errors}",
                    model.ProductId,
                    string.Join(", ", ModelState.Values
                        .SelectMany(v => v.Errors)
                        .Select(e => e.ErrorMessage)));

                try
                {
                    model.ExistingImages = await _context.ProductImages
                        .Where(img => img.ProductId == model.ProductId)
                        .Select(img => new ProductImageViewModel
                        {
                            Id = img.Id,
                            ImagePath = img.ImagePath,
                            IsMainImage = img.IsMainImage,
                            Title = img.Title,
                            DisplayOrder = img.DisplayOrder
                        })
                        .ToListAsync();

                    return View(model);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error repopulating existing images for product {ProductId}", model.ProductId);
                    throw;
                }
            }

            try
            {
                // Look up the product
                _logger.LogDebug("Looking up product {ProductId}", model.ProductId);
                var product = await _context.Products2.FindAsync(model.ProductId);
                if (product == null)
                {
                    _logger.LogError("Product {ProductId} not found in database", model.ProductId);
                    ModelState.AddModelError("", "Product not found");
                    return View(model);
                }

                // Get existing image count
                var existingImageCount = await _context.ProductImages
                    .CountAsync(img => img.ProductId == model.ProductId);

                // Check if new uploads would exceed 8 images total
                var newImageCount = model.ImageFiles?.Count ?? 0;
                if (existingImageCount + newImageCount > 8)
                {
                    _logger.LogWarning("Image count exceeds limit: {ExistingCount} existing + {NewCount} new > 8 max", 
                        existingImageCount, newImageCount);
                    ModelState.AddModelError("ImageFiles", $"Total image count cannot exceed 8. You already have {existingImageCount} images.");
                    
                    model.ExistingImages = await _context.ProductImages
                        .Where(img => img.ProductId == model.ProductId)
                        .Select(img => new ProductImageViewModel
                        {
                            Id = img.Id,
                            ImagePath = img.ImagePath,
                            IsMainImage = img.IsMainImage,
                            Title = img.Title,
                            DisplayOrder = img.DisplayOrder
                        })
                        .ToListAsync();
                    
                    return View(model);
                }

                _logger.LogInformation("Updating product publishing status - Published: {IsPublished}, Featured: {IsFeatured}",
                    model.IsPublished, model.DisplayFeatured);

                // Update product
                product.IsPublished = model.IsPublished;
                product.IsAlreadyPublished = true;
                product.DisplayFeatured = model.DisplayFeatured;
                product.Description = model.MarketplaceDescription;
                product.SellingPrice = model.MarketplacePrice ?? product.SellingPrice;
                product.UpdatedAt = GetSingaporeTime();
                _context.Products2.Update(product);

                // Handle image uploads
                if (model.ImageFiles != null && model.ImageFiles.Count > 0)
                {
                    _logger.LogDebug("Processing {ImageCount} uploaded images", model.ImageFiles.Count);
                    
                    // Check if this product has any images already
                    bool hasNoExistingImages = existingImageCount == 0;

                    foreach (var file in model.ImageFiles)
                    {
                        if (file.Length > 0)
                        {
                            _logger.LogDebug("Processing image: {FileName} ({FileSize} bytes)",
                                file.FileName, file.Length);

                            try
                            {
                                var imagePath = await SaveFile(GetCurrentUserId().ToString(), file, "products");
                                _logger.LogDebug("Image saved to: {ImagePath}", imagePath);

                                var image = new ProductImage
                                {
                                    ProductId = model.ProductId,
                                    ImagePath = imagePath,
                                    IsMainImage = hasNoExistingImages && model.ImageFiles.IndexOf(file) == 0, // First image is main only if no existing images
                                    Title = model.ProductName,
                                    DisplayOrder = existingImageCount + model.ImageFiles.IndexOf(file) + 1,
                                    CreatedAt = GetSingaporeTime()
                                };

                                _context.ProductImages.Add(image);
                                _logger.LogInformation("Added new product image (Main: {IsMain})", image.IsMainImage);

                                hasNoExistingImages = false; // After first image is processed
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error saving image {FileName}", file.FileName);
                                ModelState.AddModelError("ImageFiles", $"Error saving image: {ex.Message}");
                                continue;
                            }
                        }
                    }
                }

                _logger.LogDebug("Saving changes to database");
                await _context.SaveChangesAsync();
                _logger.LogInformation("Successfully updated product publishing settings");

                TempData["SuccessMessage"] = "Product publishing settings updated successfully";
                return RedirectToAction("Index", "Inventory1");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error publishing product {ProductId} to marketplace", model.ProductId);

                // Log all model state errors if they exist
                if (!ModelState.IsValid)
                {
                    foreach (var error in ModelState.Values.SelectMany(v => v.Errors))
                    {
                        _logger.LogError("ModelState error: {ErrorMessage}", error.ErrorMessage);
                    }
                }

                ModelState.AddModelError("", "An error occurred while updating the product. Please try again.");

                try
                {
                    model.ExistingImages = await _context.ProductImages
                        .Where(img => img.ProductId == model.ProductId)
                        .Select(img => new ProductImageViewModel
                        {
                            Id = img.Id,
                            ImagePath = img.ImagePath,
                            IsMainImage = img.IsMainImage,
                            Title = img.Title,
                            DisplayOrder = img.DisplayOrder
                        })
                        .ToListAsync();
                }
                catch (Exception innerEx)
                {
                    _logger.LogError(innerEx, "Error repopulating existing images after failure");
                }

                return View(model);
            }
        }

        private async Task<string> SaveFile(string userId, IFormFile file, string subFolder)
        {
            if (file == null || file.Length == 0)
                return string.Empty;

            // Validate file extension
            var permittedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif" };
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

            if (string.IsNullOrEmpty(extension) || !permittedExtensions.Contains(extension))
            {
                throw new InvalidOperationException("Invalid file type. Only JPG, PNG, and GIF are allowed.");
            }

            // Validate file size (5MB limit)
            if (file.Length > 5 * 1024 * 1024)
            {
                throw new InvalidOperationException("File size exceeds 5MB limit.");
            }

            var userUploadsFolder = Path.Combine(_hostEnvironment.WebRootPath, "uploads", userId, subFolder);
            Directory.CreateDirectory(userUploadsFolder);

            var fileName = $"{Path.GetFileNameWithoutExtension(file.FileName)}_{Guid.NewGuid()}{extension}";
            var filePath = Path.Combine(userUploadsFolder, fileName);

            using (var fileStream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(fileStream);
            }

            return $"/uploads/{userId}/{subFolder}/{fileName}";
        }

        [HttpPost]
        public async Task<IActionResult> SetMainImage(int imageId, int productId)
        {
            try
            {
                // Reset all images for this product to not be main
                var productImages = await _context.ProductImages
                    .Where(img => img.ProductId == productId)
                    .ToListAsync();

                foreach (var img in productImages)
                {
                    img.IsMainImage = false;
                }

                // Set the selected image as main
                var mainImage = productImages.FirstOrDefault(img => img.Id == imageId);
                if (mainImage != null)
                {
                    mainImage.IsMainImage = true;
                    await _context.SaveChangesAsync();
                    return Json(new { success = true });
                }

                return Json(new { success = false, message = "Image not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting main image");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> DeleteImage(int imageId)
        {
            try
            {
                var image = await _context.ProductImages.FindAsync(imageId);
                if (image == null)
                {
                    return Json(new { success = false, message = "Image not found" });
                }

                // Delete physical file
                if (!string.IsNullOrEmpty(image.ImagePath))
                {
                    var filePath = Path.Combine(_hostEnvironment.WebRootPath, image.ImagePath.TrimStart('/'));
                    if (System.IO.File.Exists(filePath))
                    {
                        System.IO.File.Delete(filePath);
                    }
                }

                _context.ProductImages.Remove(image);
                await _context.SaveChangesAsync();

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting image");
                return Json(new { success = false, message = ex.Message });
            }
        }

        private DateTime GetSingaporeTime() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Singapore"));

        [HttpPost] // Change from [HttpGet] to [HttpPost]
        [Route("Inventory1/SendProductRequest")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendProductRequest([FromBody] ProductRequestModel request)
        {
            try
            {
                var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userIdClaim)) return Unauthorized();

                if (!int.TryParse(userIdClaim, out int userId))
                    return Unauthorized();

                var product = await _context.Products2.FindAsync(request.ProductId);
                if (product == null) return NotFound("Product not found");

                // Create the product request data
                var requestData = new
                {
                    productId = request.ProductId,
                    productName = product.ProductName,
                    quantity = request.Quantity,
                    customerName = request.CustomerName,
                    message = request.Message,
                };

                string jsonData = System.Text.Json.JsonSerializer.Serialize(requestData);

                if (product.BOId <= 0)
                    return BadRequest(new { success = false, message = "Product owner not found." });

                int receiverId = product.BOId;

                var chat = new Chat
                {
                    SenderId = userId,
                    ReceiverId = receiverId,
                    Message = "Product Request",
                    JSONString = jsonData,
                    CreatedAt = GetSingaporeTime(),
                    IsRead = false,
                    Status = ChatStatus.Request
                };

                _context.Chats.Add(chat);
                await _context.SaveChangesAsync();

                await _hubContext.Clients
                    .Users(receiverId.ToString(), userId.ToString())
                    .SendAsync("SendProductRequest2",
                        userId.ToString(),
                        receiverId.ToString(),
                        chat.Status.ToString(),
                        chat.JSONString,
                        chat.Id);

                await _hubContext.Clients
                    .Users(receiverId.ToString(), userId.ToString())
                    .SendAsync("ReceiveProductRequest",
                        chat.Id,
                        product.Id,
                        product.ProductName,
                        requestData.quantity,
                        requestData.customerName,
                        requestData.message,
                        chat.CreatedAt
                        );

                return Ok(new
                {
                    success = true,
                    message = "Product request sent successfully",
                    receiverId = receiverId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending product request");
                return StatusCode(500, new
                {
                    success = false,
                    message = "An error occurred while sending the product request"
                });
            }
        }

        public class ProductRequestModel
        {
            public int ProductId { get; set; }
            public int Quantity { get; set; }
            public string CustomerName { get; set; }
            public string Message { get; set; }
        }

        // Keep this method for backward compatibility
        private async Task<int> CalculateEarnPoints(int leadId, int productId)
        {
            // Use the new method
            return await CalculatePointsForProduct(leadId, productId);
        }

        // Keep this method for backward compatibility
        private async Task UpdateLeadData(int leadId, int productId)
        {
            // Use the new method with a single product
            var item = new SaleItemViewModel 
            { 
                ProductId = productId,
                Quantity = 1,
                Price = 0 // Fetch actual price if needed
            };
            
            // Try to get the actual product price if possible
            var product = await _context.Products2.FindAsync(productId);
            if (product != null)
            {
                item.Price = product.SellingPrice;
            }
            
            await UpdateLeadPoints(leadId, new List<SaleItemViewModel> { item });
        }

        private async Task<Leads?> FindExistingLead(string? name, string? email, string? phone)
        {
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(phone))
            {
                return null;
            }
            
            // Build base query - remove the BOId filter to find all leads regardless of creator
            var query = _context.Leads.Where(l => l.Status != Leads.LeadStatus.Deleted);
            
            // Store potential matches with priority
            List<(Leads lead, int priority)> potentialMatches = new List<(Leads, int)>();
            
            // --- Email + Phone match (highest priority: 3) ---
            if (!string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(phone))
            {
                // Normalize phone number
                string normalizedInputPhone = new string(phone.Where(c => char.IsDigit(c)).ToArray());
                if (!string.IsNullOrWhiteSpace(normalizedInputPhone))
                {
                    var leads = await query.Where(l => 
                        !string.IsNullOrWhiteSpace(l.LeadEmail) && 
                        l.LeadEmail.ToLower() == email.ToLower())
                        .ToListAsync();
                        
                    foreach (var lead in leads)
                    {
                        if (!string.IsNullOrWhiteSpace(lead.LeadPhone))
                        {
                            string normalizedLeadPhone = new string(lead.LeadPhone.Where(c => char.IsDigit(c)).ToArray());
                            if (normalizedLeadPhone == normalizedInputPhone)
                            {
                                potentialMatches.Add((lead, 3));
                            }
                        }
                    }
                }
            }
            
            // --- Email + Name match (priority: 2) ---
            if (!string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(name))
            {
                var leads = await query.Where(l => 
                    !string.IsNullOrWhiteSpace(l.LeadEmail) && 
                    l.LeadEmail.ToLower() == email.ToLower())
                    .ToListAsync();
                    
                foreach (var lead in leads)
                {
                    if (lead.LeadName.ToLower() == name.ToLower())
                    {
                        potentialMatches.Add((lead, 2));
                    }
                }
            }
            
            // --- Phone + Name match (priority: 1) ---
            if (!string.IsNullOrWhiteSpace(phone) && !string.IsNullOrWhiteSpace(name))
            {
                // Normalize phone number
                string normalizedInputPhone = new string(phone.Where(c => char.IsDigit(c)).ToArray());
                if (!string.IsNullOrWhiteSpace(normalizedInputPhone))
                {
                    var leads = await query.ToListAsync();
                    
                    foreach (var lead in leads)
                    {
                        if (!string.IsNullOrWhiteSpace(lead.LeadPhone) && lead.LeadName.ToLower() == name.ToLower())
                        {
                            string normalizedLeadPhone = new string(lead.LeadPhone.Where(c => char.IsDigit(c)).ToArray());
                            if (normalizedLeadPhone == normalizedInputPhone)
                            {
                                potentialMatches.Add((lead, 1));
                            }
                        }
                    }
                }
            }
            
            // --- Individual field exact matches (if no combined matches found) ---
            if (!potentialMatches.Any())
            {
                // Email exact match
                if (!string.IsNullOrWhiteSpace(email))
                {
                    var lead = await query.FirstOrDefaultAsync(l => 
                        !string.IsNullOrWhiteSpace(l.LeadEmail) && 
                        l.LeadEmail.ToLower() == email.ToLower());
                        
                    if (lead != null)
                    {
                        potentialMatches.Add((lead, 0));
                    }
                }
                
                // Phone exact match
                if (!string.IsNullOrWhiteSpace(phone) && !potentialMatches.Any())
                {
                    string normalizedInputPhone = new string(phone.Where(c => char.IsDigit(c)).ToArray());
                    if (!string.IsNullOrWhiteSpace(normalizedInputPhone))
                    {
                        var leads = await query.ToListAsync();
                        var lead = leads.FirstOrDefault(l => 
                            !string.IsNullOrWhiteSpace(l.LeadPhone) && 
                            new string(l.LeadPhone.Where(c => char.IsDigit(c)).ToArray()) == normalizedInputPhone);
                            
                        if (lead != null)
                        {
                            potentialMatches.Add((lead, 0));
                        }
                    }
                }
                
                // Name exact match (lowest priority)
                if (!string.IsNullOrWhiteSpace(name) && !potentialMatches.Any())
                {
                    var lead = await query.FirstOrDefaultAsync(l => l.LeadName.ToLower() == name.ToLower());
                    if (lead != null)
                    {
                        potentialMatches.Add((lead, 0));
                    }
                }
            }
            
            // Return the lead with the highest priority
            if (potentialMatches.Any())
            {
                var bestMatch = potentialMatches.OrderByDescending(m => m.priority).First();
                _logger.LogInformation("Found existing lead by priority {Priority}: {LeadId}, {LeadName}", 
                    bestMatch.priority, bestMatch.lead.Id, bestMatch.lead.LeadName);
                return bestMatch.lead;
            }
            
            // No match found
            return null;
        }
    }
}