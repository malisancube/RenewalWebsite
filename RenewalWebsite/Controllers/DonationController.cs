﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RenewalWebsite.Services;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Identity;
using RenewalWebsite.Models;
using Stripe;
using RenewalWebsite.Helpers;
using Microsoft.Extensions.Logging;
using RenewalWebsite.Utility;

namespace RenewalWebsite.Controllers
{
    [Authorize]
    public class DonationController : Controller
    {
        private const string DonationCaption = "Renewal Center donation";
        private readonly IDonationService _donationService;
        private readonly IOptions<StripeSettings> _stripeSettings;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IOptions<ExchangeRate> _exchangeSettings;
        private readonly ICampaignService _campaignService;
        private readonly ILogger<DonationController> _logger;

        public DonationController(
            UserManager<ApplicationUser> userManager,
            IDonationService donationService,
            IOptions<StripeSettings> stripeSettings,
            IOptions<ExchangeRate> exchangeSettings,
            ICampaignService campaignService,
            ILogger<DonationController> logger)
        {
            _userManager = userManager;
            _donationService = donationService;
            _stripeSettings = stripeSettings;
            _exchangeSettings = exchangeSettings;
            _campaignService = campaignService;
            _logger = logger;
        }

        [Route("Donation/Payment")]
        [HttpGet]
        public ActionResult payment()
        {
            return RedirectToAction("index", "home");
        }

        [Route("Donation/Payment/{id}")]
        public async Task<IActionResult> Payment(int id)
        {
            try
            {
                var user = await GetCurrentUserAsync();
                var donation = _donationService.GetById(id);
                var detail = (DonationViewModel)donation;
                detail.DonationOptions = _donationService.DonationOptions;

                // Check for existing customer
                // edit = 1 means user wants to edit the credit card information
                if (!string.IsNullOrEmpty(user.StripeCustomerId))
                {
                    try
                    {
                        var CustomerService = new StripeCustomerService(_stripeSettings.Value.SecretKey);
                        StripeCustomer objStripeCustomer = CustomerService.Get(user.StripeCustomerId);
                        StripeCard objStripeCard = null;

                        if (objStripeCustomer.Sources != null && objStripeCustomer.Sources.TotalCount > 0 && objStripeCustomer.Sources.Data.Any())
                        {
                            objStripeCard = objStripeCustomer.Sources.Data.FirstOrDefault().Card;
                        }

                        if (objStripeCard != null && !string.IsNullOrEmpty(objStripeCard.Id))
                        {
                            var objCustomerRePaymentViewModel = new CustomerRePaymentViewModel
                            {
                                Name = user.FullName,
                                AddressLine1 = user.AddressLine1,
                                AddressLine2 = user.AddressLine2,
                                City = user.City,
                                State = user.State,
                                Country = user.Country,
                                Zip = user.Zip,
                                DonationId = donation.Id,
                                Description = detail.GetDescription(),
                                Frequency = detail.GetCycle(),
                                Amount = detail.GetDisplayAmount(),
                                Last4Digit = objStripeCard.Last4,
                                CardId = objStripeCard.Id,
                                //Currency = (objStripeCustomer.Currency + "").ToUpper(),
                                DisableCurrencySelection = string.IsNullOrEmpty(objStripeCustomer.Currency) ? "0" : "1",
                                ExchangeRate = _exchangeSettings.Value.Rate,
                                IsCustom = detail.DonationOptions.Where(a => a.Id == donation.SelectedAmount).Select(a => a.IsCustom).FirstOrDefault()
                            };

                            return View("RePayment", objCustomerRePaymentViewModel);
                        }
                    }
                    catch (StripeException sex)
                    {
                        _logger.LogError((int)LoggingEvents.GET_ITEM, sex.Message);
                        ModelState.AddModelError("CustomerNotFound", sex.Message);
                    }

                }

                var model = new CustomerPaymentViewModel
                {
                    Name = user.FullName,
                    AddressLine1 = user.AddressLine1,
                    AddressLine2 = user.AddressLine2,
                    City = user.City,
                    State = user.State,
                    Country = string.IsNullOrEmpty(user.Country) ? "US" : user.Country,
                    Zip = user.Zip,
                    DonationId = donation.Id,
                    Description = detail.GetDescription(),
                    Frequency = detail.GetCycle(),
                    Amount = detail.GetDisplayAmount(),
                    ExchangeRate = _exchangeSettings.Value.Rate,
                    IsCustom = detail.DonationOptions.Where(a => a.Id == donation.SelectedAmount).Select(a => a.IsCustom).FirstOrDefault()
                    //Currency = "USD"
                };

                return View("Payment", model);
            }
            catch (Exception ex)
            {
                _logger.LogError((int)LoggingEvents.GET_ITEM, ex.Message);
                return View(null);
            }
        }

        [HttpPost]
        public async Task<IActionResult> Payment(CustomerPaymentViewModel payment)
        {
            try
            {
                var user = await GetCurrentUserAsync();

                if (!ModelState.IsValid)
                {
                    payment.ExchangeRate = _exchangeSettings.Value.Rate;
                    return View(payment);
                }

                var customerService = new StripeCustomerService(_stripeSettings.Value.SecretKey);
                var donation = _donationService.GetById(payment.DonationId);

                // Construct payment
                if (string.IsNullOrEmpty(user.StripeCustomerId))
                {
                    var customer = new StripeCustomerCreateOptions
                    {
                        Email = user.Email,
                        Description = $"{user.Email} {user.Id}",
                        SourceCard = new SourceCard
                        {
                            Name = payment.Name,
                            Number = payment.CardNumber,
                            Cvc = payment.Cvc,
                            ExpirationMonth = payment.ExpiryMonth,
                            ExpirationYear = payment.ExpiryYear,
                            StatementDescriptor = _stripeSettings.Value.StatementDescriptor,
                            Description = DonationCaption,
                            AddressLine1 = payment.AddressLine1,
                            AddressLine2 = payment.AddressLine2,
                            AddressCity = payment.City,
                            AddressState = payment.State,
                            AddressCountry = payment.Country,
                            AddressZip = payment.Zip
                        }
                    };
                    var stripeCustomer = customerService.Create(customer);
                    user.StripeCustomerId = stripeCustomer.Id;
                }
                else
                {
                    //Check for existing credit card, if new credit card number is same as exiting credit card then we delete the existing
                    //Credit card information so new card gets generated automatically as default card.
                    try
                    {
                        var ExistingCustomer = customerService.Get(user.StripeCustomerId);
                        if (ExistingCustomer.Sources != null && ExistingCustomer.Sources.TotalCount > 0 && ExistingCustomer.Sources.Data.Any())
                        {
                            var cardService = new StripeCardService(_stripeSettings.Value.SecretKey);
                            foreach (var cardSource in ExistingCustomer.Sources.Data)
                            {
                                cardService.Delete(user.StripeCustomerId, cardSource.Card.Id);
                            }
                        }
                    }
                    catch (Exception exSub)
                    {
                        _logger.LogError((int)LoggingEvents.GET_CUSTOMER, exSub.Message);
                        return RedirectToAction("Error", "Error500", new ErrorViewModel() { Error = exSub.Message });
                    }

                    var customer = new StripeCustomerUpdateOptions
                    {
                        SourceCard = new SourceCard
                        {
                            Name = payment.Name,
                            Number = payment.CardNumber,
                            Cvc = payment.Cvc,
                            ExpirationMonth = payment.ExpiryMonth,
                            ExpirationYear = payment.ExpiryYear,
                            StatementDescriptor = _stripeSettings.Value.StatementDescriptor,
                            Description = DonationCaption,
                            AddressLine1 = payment.AddressLine1,
                            AddressLine2 = payment.AddressLine2,
                            AddressCity = payment.City,
                            AddressState = payment.State,
                            AddressCountry = payment.Country,
                            AddressZip = payment.Zip
                        }
                    };

                    var stripeCustomer = customerService.Update(user.StripeCustomerId, customer);
                    user.StripeCustomerId = stripeCustomer.Id;
                }

                user.FullName = payment.Name;
                user.AddressLine1 = payment.AddressLine1;
                user.AddressLine2 = payment.AddressLine2;
                user.City = payment.City;
                user.State = payment.State;
                user.Country = payment.Country;
                user.Zip = payment.Zip;
                await _userManager.UpdateAsync(user);


                // Add customer to Stripe
                if (EnumInfo<PaymentCycle>.GetValue(donation.CycleId) == PaymentCycle.OneTime)
                {
                    var model = (DonationViewModel)donation;
                    model.DonationOptions = _donationService.DonationOptions;

                    var charges = new StripeChargeService(_stripeSettings.Value.SecretKey);

                    // Charge the customer
                    var charge = charges.Create(new StripeChargeCreateOptions
                    {
                        //Amount = payment.IsCustom ? Convert.ToInt32(Math.Round((model.GetAmount()), 2)) : Convert.ToInt32(Math.Round((model.GetAmount() / _exchangeSettings.Value.Rate), 2)),
                        Amount = Convert.ToInt32(model.GetAmount()),
                        Description = DonationCaption,
                        Currency = "usd",//payment.Currency.ToLower(),
                        CustomerId = user.StripeCustomerId,
                        //ReceiptEmail = user.Email,
                        StatementDescriptor = _stripeSettings.Value.StatementDescriptor,
                    });

                    if (charge.Paid)
                    {
                        //decimal value = payment.IsCustom ? Math.Round(model.GetDisplayAmount(), 2) : Math.Round((model.GetDisplayAmount() / _exchangeSettings.Value.Rate), 2);
                        var completedMessage = new CompletedViewModel
                        {
                            Message = $"Your card was charged successfully. Thank you for your kind gift of ${model.GetDisplayAmount()}.",
                            HasSubscriptions = false
                        };
                        return RedirectToAction("Thanks", completedMessage);
                        //return View("Thanks", completedMessage);
                    }
                    //return RedirectToAction("Error", "Error", new ErrorViewModel() { Error = "Error" });
                    return View("Error");
                }

                // Add to existing subscriptions and charge 
                donation.currency = "usd"; //payment.Currency;
                var plan = _donationService.GetOrCreatePlan(donation);

                var subscriptionService = new StripeSubscriptionService(_stripeSettings.Value.SecretKey);
                var result = subscriptionService.Create(user.StripeCustomerId, plan.Id);
                if (result != null)
                {
                    var completedMessage = new CompletedViewModel
                    {
                        Message = $"Your gift ${result.StripePlan.Name.Split("_")[1]} will repeat {result.StripePlan.Name.Split("_")[0]}. To manage or cancel your subscription anytime, follow the link below.",
                        HasSubscriptions = true
                    };
                    return RedirectToAction("Thanks", completedMessage);
                    //return View("Thanks", completedMessage);
                }
            }
            catch (StripeException sex)
            {
                _logger.LogError((int)LoggingEvents.INSERT_ITEM, sex.Message);
                if (sex.Message.ToLower().Contains("customer"))
                {
                    return RedirectToAction("Error", "Error500", new ErrorViewModel() { Error = sex.Message });
                }
                else
                {
                    ModelState.AddModelError("error", sex.Message);
                    payment.ExchangeRate = _exchangeSettings.Value.Rate;
                    return View(payment);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError((int)LoggingEvents.INSERT_ITEM, ex.Message);
                //return RedirectToAction("Error", "Error", new ErrorViewModel() { Error = ex.Message });
                return View("Error");
            }
            //return RedirectToAction("Error", "Error", new ErrorViewModel() { Error = "Error" });
            return View("Error");
        }

        [Route("Donation/Payment/{id}/{edit?}")]
        public async Task<IActionResult> Payment(int id, int edit)
        {
            try
            {
                var user = await GetCurrentUserAsync();
                var donation = _donationService.GetById(id);
                var detail = (DonationViewModel)donation;
                detail.DonationOptions = _donationService.DonationOptions;
                CustomerPaymentViewModel model = new CustomerPaymentViewModel();

                try
                {
                    var customerService = new StripeCustomerService(_stripeSettings.Value.SecretKey);
                    var ExistingCustomer = customerService.Get(user.StripeCustomerId);
                    model = new CustomerPaymentViewModel
                    {
                        Name = user.FullName,
                        AddressLine1 = user.AddressLine1,
                        AddressLine2 = user.AddressLine2,
                        City = user.City,
                        State = user.State,
                        Country = user.Country,
                        Zip = user.Zip,
                        DonationId = donation.Id,
                        Description = detail.GetDescription(),
                        Frequency = detail.GetCycle(),
                        Amount = detail.GetDisplayAmount(),
                        //Currency = string.IsNullOrEmpty(ExistingCustomer.Currency) ? string.Empty : ExistingCustomer.Currency.ToUpper(),
                        DisableCurrencySelection = "1", // Disable currency selection for already created customer as stripe only allow same currency for one customer,
                        ExchangeRate = _exchangeSettings.Value.Rate,
                        IsCustom = detail.DonationOptions.Where(a => a.Id == donation.SelectedAmount).Select(a => a.IsCustom).FirstOrDefault()
                    };
                }
                catch (StripeException sex)
                {
                    _logger.LogError((int)LoggingEvents.GET_CUSTOMER, sex.Message);
                    ModelState.AddModelError("CustomerNotFound", sex.Message);
                }

                return View("Payment", model);
            }
            catch(Exception ex)
            {
                _logger.LogError((int)LoggingEvents.GET_CUSTOMER, ex.Message);
                return View(null);
            }
        }

        [HttpPost]
        public async Task<IActionResult> RePayment(CustomerRePaymentViewModel payment)
        {
            try
            {
                var user = await GetCurrentUserAsync();

                if (!ModelState.IsValid)
                {
                    payment.ExchangeRate = _exchangeSettings.Value.Rate;
                    return View(payment);
                }

                var customerService = new StripeCustomerService(_stripeSettings.Value.SecretKey);
                var donation = _donationService.GetById(payment.DonationId);

                user.FullName = payment.Name;
                user.AddressLine1 = payment.AddressLine1;
                user.AddressLine2 = payment.AddressLine2;
                user.City = payment.City;
                user.State = payment.State;
                user.Country = payment.Country;
                user.Zip = payment.Zip;
                await _userManager.UpdateAsync(user);

                // Add customer to Stripe
                if (EnumInfo<PaymentCycle>.GetValue(donation.CycleId) == PaymentCycle.OneTime)
                {
                    var model = (DonationViewModel)donation;
                    model.DonationOptions = _donationService.DonationOptions;

                    var charges = new StripeChargeService(_stripeSettings.Value.SecretKey);

                    // Charge the customer
                    var charge = charges.Create(new StripeChargeCreateOptions
                    {
                        //Amount = payment.IsCustom ? Convert.ToInt32(Math.Round((model.GetAmount()), 2)) : Convert.ToInt32(Math.Round((model.GetAmount() / _exchangeSettings.Value.Rate), 2)),
                        Amount = Convert.ToInt32(model.GetAmount()),
                        Description = DonationCaption,
                        Currency = "usd", //payment.Currency.ToLower(),
                        CustomerId = user.StripeCustomerId,
                        //ReceiptEmail = user.Email,
                        StatementDescriptor = _stripeSettings.Value.StatementDescriptor,
                    });

                    if (charge.Paid)
                    {
                        //decimal value = payment.IsCustom ? Math.Round(model.GetDisplayAmount(), 2) : Math.Round((model.GetDisplayAmount() / _exchangeSettings.Value.Rate), 2);
                        var completedMessage = new CompletedViewModel
                        {
                            Message = $"Your card was charged successfully. Thank you for your kind gift of ${model.GetDisplayAmount()}.",
                            HasSubscriptions = false
                        };
                        return RedirectToAction("Thanks", completedMessage);
                        //return View("Thanks", completedMessage);
                    }
                    //return RedirectToAction("Error", "Error", new ErrorViewModel() { Error = "Error" });
                    return View("Error");
                }
                donation.currency = "usd";//payment.Currency;
                // Add to existing subscriptions and charge 
                var plan = _donationService.GetOrCreatePlan(donation);

                var subscriptionService = new StripeSubscriptionService(_stripeSettings.Value.SecretKey);
                var result = subscriptionService.Create(user.StripeCustomerId, plan.Id);
                if (result != null)
                {
                    var completedMessage = new CompletedViewModel
                    {
                        Message = $"Your gift ${result.StripePlan.Name.Split("_")[1]} will repeat {result.StripePlan.Name.Split("_")[0]}. To manage or cancel your subscription anytime, follow the link below.",
                        HasSubscriptions = true
                    };
                    return RedirectToAction("Thanks", completedMessage);
                    //return View("Thanks", completedMessage);
                }
            }
            catch (StripeException sex)
            {
                _logger.LogError((int)LoggingEvents.INSERT_ITEM, sex.Message);
                if (sex.Message.ToLower().Contains("customer"))
                {
                    return RedirectToAction("Error", "Error500", new ErrorViewModel() { Error = sex.Message });
                }
                else
                {
                    ModelState.AddModelError("error", sex.Message);
                    payment.ExchangeRate = _exchangeSettings.Value.Rate;
                    return View(payment);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError((int)LoggingEvents.INSERT_ITEM, ex.Message);
                //return RedirectToAction("Error", "Error", new ErrorViewModel() { Error = "Error" });
                return View("Error");
            }
            //return RedirectToAction("Error", "Error", new ErrorViewModel() { Error = "Error" });
            return View("Error");
        }

        private Task<ApplicationUser> GetCurrentUserAsync()
        {
            return _userManager.GetUserAsync(HttpContext.User);
            //Source src = new Source();
            //src.Type = SourceType.
        }

        public ActionResult Thanks(CompletedViewModel model)
        {
            return View(model);
        }

        [Route("Donation/Payment/campaign")]
        [HttpGet]
        public ActionResult campaignPayment()
        {
            return RedirectToAction("index", "home");
        }

        [Route("Donation/Payment/campaign/{id}")]
        public async Task<IActionResult> CampaignPayment(int id)
        {
            try
            {
                var user = await GetCurrentUserAsync();
                var donation = _campaignService.GetById(id);
                var detail = (DonationViewModel)donation;
                detail.DonationOptions = _campaignService.DonationOptions;

                // Check for existing customer
                // edit = 1 means user wants to edit the credit card information
                if (!string.IsNullOrEmpty(user.StripeCustomerId))
                {
                    try
                    {
                        var CustomerService = new StripeCustomerService(_stripeSettings.Value.SecretKey);
                        StripeCustomer objStripeCustomer = CustomerService.Get(user.StripeCustomerId);
                        StripeCard objStripeCard = null;

                        if (objStripeCustomer.Sources != null && objStripeCustomer.Sources.TotalCount > 0 && objStripeCustomer.Sources.Data.Any())
                        {
                            objStripeCard = objStripeCustomer.Sources.Data.FirstOrDefault().Card;
                        }

                        if (objStripeCard != null && !string.IsNullOrEmpty(objStripeCard.Id))
                        {
                            var objCustomerRePaymentViewModel = new CustomerRePaymentViewModel
                            {
                                Name = user.FullName,
                                AddressLine1 = user.AddressLine1,
                                AddressLine2 = user.AddressLine2,
                                City = user.City,
                                State = user.State,
                                Country = user.Country,
                                Zip = user.Zip,
                                DonationId = donation.Id,
                                Description = detail.GetDescription(),
                                Frequency = detail.GetCycle(),
                                Amount = detail.GetDisplayAmount(),
                                Last4Digit = objStripeCard.Last4,
                                CardId = objStripeCard.Id,
                                //Currency = (objStripeCustomer.Currency + "").ToUpper(),
                                DisableCurrencySelection = string.IsNullOrEmpty(objStripeCustomer.Currency) ? "0" : "1",
                                ExchangeRate = _exchangeSettings.Value.Rate,
                                IsCustom = detail.DonationOptions.Where(a => a.Id == donation.SelectedAmount).Select(a => a.IsCustom).FirstOrDefault()
                            };

                            return View("CampaignRePayment", objCustomerRePaymentViewModel);
                        }
                    }
                    catch (StripeException sex)
                    {
                        _logger.LogError((int)LoggingEvents.GET_CUSTOMER, "Customer not found.");
                        ModelState.AddModelError("CustomerNotFound", sex.Message);
                    }
                }

                var model = new CustomerPaymentViewModel
                {
                    Name = user.FullName,
                    AddressLine1 = user.AddressLine1,
                    AddressLine2 = user.AddressLine2,
                    City = user.City,
                    State = user.State,
                    Country = string.IsNullOrEmpty(user.Country) ? "US" : user.Country,
                    Zip = user.Zip,
                    DonationId = donation.Id,
                    Description = detail.GetDescription(),
                    Frequency = detail.GetCycle(),
                    Amount = detail.GetDisplayAmount(),
                    ExchangeRate = _exchangeSettings.Value.Rate,
                    IsCustom = detail.DonationOptions.Where(a => a.Id == donation.SelectedAmount).Select(a => a.IsCustom).FirstOrDefault()
                    //Currency = "USD"
                };

                return View("CampaignPayment", model);
            }
            catch(Exception ex)
            {
                _logger.LogError((int)LoggingEvents.GET_ITEM, ex.Message);
                return View(null);
            }
        }

        [HttpPost]
        public async Task<IActionResult> CampaignPayment(CustomerPaymentViewModel payment)
        {
            try
            {
                var user = await GetCurrentUserAsync();

                if (!ModelState.IsValid)
                {
                    payment.ExchangeRate = _exchangeSettings.Value.Rate;
                    return View(payment);
                }

                var customerService = new StripeCustomerService(_stripeSettings.Value.SecretKey);
                var donation = _campaignService.GetById(payment.DonationId);

                // Construct payment
                if (string.IsNullOrEmpty(user.StripeCustomerId))
                {
                    var customer = new StripeCustomerCreateOptions
                    {
                        Email = user.Email,
                        Description = $"{user.Email} {user.Id}",
                        SourceCard = new SourceCard
                        {
                            Name = payment.Name,
                            Number = payment.CardNumber,
                            Cvc = payment.Cvc,
                            ExpirationMonth = payment.ExpiryMonth,
                            ExpirationYear = payment.ExpiryYear,
                            StatementDescriptor = _stripeSettings.Value.StatementDescriptor,
                            Description = DonationCaption,
                            AddressLine1 = payment.AddressLine1,
                            AddressLine2 = payment.AddressLine2,
                            AddressCity = payment.City,
                            AddressState = payment.State,
                            AddressCountry = payment.Country,
                            AddressZip = payment.Zip
                        }
                    };
                    var stripeCustomer = customerService.Create(customer);
                    user.StripeCustomerId = stripeCustomer.Id;
                }
                else
                {
                    //Check for existing credit card, if new credit card number is same as exiting credit card then we delete the existing
                    //Credit card information so new card gets generated automatically as default card.
                    try
                    {
                        var ExistingCustomer = customerService.Get(user.StripeCustomerId);
                        if (ExistingCustomer.Sources != null && ExistingCustomer.Sources.TotalCount > 0 && ExistingCustomer.Sources.Data.Any())
                        {
                            var cardService = new StripeCardService(_stripeSettings.Value.SecretKey);
                            foreach (var cardSource in ExistingCustomer.Sources.Data)
                            {
                                cardService.Delete(user.StripeCustomerId, cardSource.Card.Id);
                            }
                        }
                    }
                    catch (Exception exSub)
                    {
                        _logger.LogError((int)LoggingEvents.INSERT_ITEM, exSub.Message);
                        return RedirectToAction("Error", "Error500", new ErrorViewModel() { Error = exSub.Message });
                    }

                    var customer = new StripeCustomerUpdateOptions
                    {
                        SourceCard = new SourceCard
                        {
                            Name = payment.Name,
                            Number = payment.CardNumber,
                            Cvc = payment.Cvc,
                            ExpirationMonth = payment.ExpiryMonth,
                            ExpirationYear = payment.ExpiryYear,
                            StatementDescriptor = _stripeSettings.Value.StatementDescriptor,
                            Description = DonationCaption,
                            AddressLine1 = payment.AddressLine1,
                            AddressLine2 = payment.AddressLine2,
                            AddressCity = payment.City,
                            AddressState = payment.State,
                            AddressCountry = payment.Country,
                            AddressZip = payment.Zip
                        }
                    };

                    var stripeCustomer = customerService.Update(user.StripeCustomerId, customer);
                    user.StripeCustomerId = stripeCustomer.Id;
                }

                user.FullName = payment.Name;
                user.AddressLine1 = payment.AddressLine1;
                user.AddressLine2 = payment.AddressLine2;
                user.City = payment.City;
                user.State = payment.State;
                user.Country = payment.Country;
                user.Zip = payment.Zip;
                await _userManager.UpdateAsync(user);


                // Add customer to Stripe
                if (EnumInfo<PaymentCycle>.GetValue(donation.CycleId) == PaymentCycle.OneTime)
                {
                    var model = (DonationViewModel)donation;
                    model.DonationOptions = _campaignService.DonationOptions;

                    var charges = new StripeChargeService(_stripeSettings.Value.SecretKey);

                    // Charge the customer
                    var charge = charges.Create(new StripeChargeCreateOptions
                    {
                        //Amount = payment.IsCustom ? Convert.ToInt32(Math.Round((model.GetAmount()), 2)) : Convert.ToInt32(Math.Round((model.GetAmount() / _exchangeSettings.Value.Rate), 2)),
                        Amount = Convert.ToInt32(model.GetAmount()),
                        Description = DonationCaption,
                        Currency = "usd",//payment.Currency.ToLower(),
                        CustomerId = user.StripeCustomerId,
                        //ReceiptEmail = user.Email,
                        StatementDescriptor = _stripeSettings.Value.StatementDescriptor,
                    });

                    if (charge.Paid)
                    {
                        //decimal value = payment.IsCustom ? Math.Round(model.GetDisplayAmount(), 2) : Math.Round((model.GetDisplayAmount() / _exchangeSettings.Value.Rate), 2);
                        var completedMessage = new CompletedViewModel
                        {
                            Message = $"Your card was charged successfully. Thank you for your kind gift of ${model.GetDisplayAmount()}.",
                            HasSubscriptions = false
                        };
                        return RedirectToAction("Thanks", completedMessage);
                        //return View("Thanks", completedMessage);
                    }
                    //return RedirectToAction("Error", "Error", new ErrorViewModel() { Error = "Error" });
                    return View("Error");
                }

                // Add to existing subscriptions and charge 
                donation.currency = "usd"; //payment.Currency;
                var plan = _campaignService.GetOrCreatePlan(donation);

                var subscriptionService = new StripeSubscriptionService(_stripeSettings.Value.SecretKey);
                var result = subscriptionService.Create(user.StripeCustomerId, plan.Id);
                if (result != null)
                {
                    var completedMessage = new CompletedViewModel
                    {
                        Message = $"Your gift ${result.StripePlan.Name.Split("_")[1]} will repeat {result.StripePlan.Name.Split("_")[0]}. To manage or cancel your subscription anytime, follow the link below.",
                        HasSubscriptions = true
                    };
                    return RedirectToAction("Thanks", completedMessage);
                    //return View("Thanks", completedMessage);
                }
            }
            catch (StripeException sex)
            {
                _logger.LogError((int)LoggingEvents.INSERT_ITEM, sex.Message);
                if (sex.Message.ToLower().Contains("customer"))
                {
                    return RedirectToAction("Error", "Error500", new ErrorViewModel() { Error = sex.Message });
                }
                else
                {
                    ModelState.AddModelError("error", sex.Message);
                    payment.ExchangeRate = _exchangeSettings.Value.Rate;
                    return View(payment);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError((int)LoggingEvents.INSERT_ITEM, ex.Message);
                //return RedirectToAction("Error", "Error", new ErrorViewModel() { Error = ex.Message });
                return View("Error");
            }
            //return RedirectToAction("Error", "Error", new ErrorViewModel() { Error = "Error" });
            return View("Error");
        }

        [Route("Donation/Payment/campaign/{id}/{edit?}")]
        public async Task<IActionResult> CampaignPayment(int id, int edit)
        {
            try
            {
                var user = await GetCurrentUserAsync();
                var donation = _campaignService.GetById(id);
                var detail = (DonationViewModel)donation;
                detail.DonationOptions = _campaignService.DonationOptions;
                CustomerPaymentViewModel model = new CustomerPaymentViewModel();

                try
                {
                    var customerService = new StripeCustomerService(_stripeSettings.Value.SecretKey);
                    var ExistingCustomer = customerService.Get(user.StripeCustomerId);
                    model = new CustomerPaymentViewModel
                    {
                        Name = user.FullName,
                        AddressLine1 = user.AddressLine1,
                        AddressLine2 = user.AddressLine2,
                        City = user.City,
                        State = user.State,
                        Country = user.Country,
                        Zip = user.Zip,
                        DonationId = donation.Id,
                        Description = detail.GetDescription(),
                        Frequency = detail.GetCycle(),
                        Amount = detail.GetDisplayAmount(),
                        //Currency = string.IsNullOrEmpty(ExistingCustomer.Currency) ? string.Empty : ExistingCustomer.Currency.ToUpper(),
                        DisableCurrencySelection = "1", // Disable currency selection for already created customer as stripe only allow same currency for one customer,
                        ExchangeRate = _exchangeSettings.Value.Rate,
                        IsCustom = detail.DonationOptions.Where(a => a.Id == donation.SelectedAmount).Select(a => a.IsCustom).FirstOrDefault()
                    };
                }
                catch (StripeException sex)
                {
                    _logger.LogError((int)LoggingEvents.GET_CUSTOMER, sex.Message);
                    ModelState.AddModelError("CustomerNotFound", sex.Message);
                }

                return View("CampaignPayment", model);
            }
            catch(Exception ex)
            {
                _logger.LogError((int)LoggingEvents.INSERT_ITEM, ex.Message);
                return View(null);
            }
        }

        [HttpPost]
        public async Task<IActionResult> CampaignRePayment(CustomerRePaymentViewModel payment)
        {
            try
            {
                var user = await GetCurrentUserAsync();

                if (!ModelState.IsValid)
                {
                    payment.ExchangeRate = _exchangeSettings.Value.Rate;
                    return View(payment);
                }

                var customerService = new StripeCustomerService(_stripeSettings.Value.SecretKey);
                var donation = _campaignService.GetById(payment.DonationId);

                user.FullName = payment.Name;
                user.AddressLine1 = payment.AddressLine1;
                user.AddressLine2 = payment.AddressLine2;
                user.City = payment.City;
                user.State = payment.State;
                user.Country = payment.Country;
                user.Zip = payment.Zip;
                await _userManager.UpdateAsync(user);

                // Add customer to Stripe
                if (EnumInfo<PaymentCycle>.GetValue(donation.CycleId) == PaymentCycle.OneTime)
                {
                    var model = (DonationViewModel)donation;
                    model.DonationOptions = _campaignService.DonationOptions;

                    var charges = new StripeChargeService(_stripeSettings.Value.SecretKey);

                    // Charge the customer
                    var charge = charges.Create(new StripeChargeCreateOptions
                    {
                        //Amount = payment.IsCustom ? Convert.ToInt32(Math.Round((model.GetAmount()), 2)) : Convert.ToInt32(Math.Round((model.GetAmount() / _exchangeSettings.Value.Rate), 2)),
                        Amount = Convert.ToInt32(model.GetAmount()),
                        Description = DonationCaption,
                        Currency = "usd", //payment.Currency.ToLower(),
                        CustomerId = user.StripeCustomerId,
                        //ReceiptEmail = user.Email,
                        StatementDescriptor = _stripeSettings.Value.StatementDescriptor,
                    });

                    if (charge.Paid)
                    {
                        //decimal value = payment.IsCustom ? Math.Round(model.GetDisplayAmount(), 2) : Math.Round((model.GetDisplayAmount() / _exchangeSettings.Value.Rate), 2);
                        var completedMessage = new CompletedViewModel
                        {
                            Message = $"Your card was charged successfully. Thank you for your kind gift of ${model.GetDisplayAmount()}.",
                            HasSubscriptions = false
                        };
                        return RedirectToAction("Thanks", completedMessage);
                        //return View("Thanks", completedMessage);
                    }
                    //return RedirectToAction("Error", "Error", new ErrorViewModel() { Error = "Error" });
                    return View("Error");
                }
                donation.currency = "usd";//payment.Currency;
                // Add to existing subscriptions and charge 
                var plan = _campaignService.GetOrCreatePlan(donation);

                var subscriptionService = new StripeSubscriptionService(_stripeSettings.Value.SecretKey);
                var result = subscriptionService.Create(user.StripeCustomerId, plan.Id);
                if (result != null)
                {
                    var completedMessage = new CompletedViewModel
                    {
                        Message = $"Your gift ${result.StripePlan.Name.Split("_")[1]} will repeat {result.StripePlan.Name.Split("_")[0]}. To manage or cancel your subscription anytime, follow the link below.",
                        HasSubscriptions = true
                    };
                    return RedirectToAction("Thanks", completedMessage);
                    //return View("Thanks", completedMessage);
                }
            }
            catch (StripeException sex)
            {
                _logger.LogError((int)LoggingEvents.INSERT_ITEM, sex.Message);
                if (sex.Message.ToLower().Contains("customer"))
                {
                    return RedirectToAction("Error", "Error500", new ErrorViewModel() { Error = sex.Message });
                }
                else
                {
                    ModelState.AddModelError("error", sex.Message);
                    payment.ExchangeRate = _exchangeSettings.Value.Rate;
                    return View(payment);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError((int)LoggingEvents.INSERT_ITEM, ex.Message);
                //return RedirectToAction("Error", "Error", new ErrorViewModel() { Error = "Error" });
                return View("Error");
            }
            //return RedirectToAction("Error", "Error", new ErrorViewModel() { Error = "Error" });
            return View("Error");
        }
    }
}