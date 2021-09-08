﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using LNURL;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using NBitcoin.Crypto;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer
{
    [Route("~/{cryptoCode}/[controller]/")]
    public class LNURLController : Controller
    {
        private readonly InvoiceRepository _invoiceRepository;
        private readonly EventAggregator _eventAggregator;
        private readonly BTCPayNetworkProvider _btcPayNetworkProvider;
        private readonly LightningLikePaymentHandler _lightningLikePaymentHandler;
        private readonly StoreRepository _storeRepository;
        private readonly InvoiceController _invoiceController;
        private static LightningAddressSettings _lightningAddressSettings;
        private readonly SettingsRepository _settingsRepository;

        public LNURLController(InvoiceRepository invoiceRepository,
            EventAggregator eventAggregator,
            BTCPayNetworkProvider btcPayNetworkProvider,
            LightningLikePaymentHandler lightningLikePaymentHandler,
            StoreRepository storeRepository,
            InvoiceController invoiceController, SettingsRepository settingsRepository)
        {
            _invoiceRepository = invoiceRepository;
            _eventAggregator = eventAggregator;
            _btcPayNetworkProvider = btcPayNetworkProvider;
            _lightningLikePaymentHandler = lightningLikePaymentHandler;
            _storeRepository = storeRepository;
            _invoiceController = invoiceController;
            _settingsRepository = settingsRepository;
            _lightningAddressSettings = settingsRepository.GetSettingAsync<LightningAddressSettings>().Result ??
                                        new LightningAddressSettings();
        }

        //
        // [HttpGet("pay/app/{appId}")]
        // public async Task<IActionResult> GetLNURLForApp(string cryptoCode, string appId, string itemCode = null,
        //     long? amount = null)
        // {
        //     var network = _btcPayNetworkProvider.GetNetwork<BTCPayNetwork>(cryptoCode);
        //     if (network is null || !network.SupportLightning)
        //     {
        //         return NotFound();
        //     }
        //
        //     var app = await _appService.GetApp(appId, null, true);
        //     if (app is null)
        //     {
        //         return NotFound();
        //     }
        //
        //     var store = app.StoreData;
        //     if (store is null)
        //     {
        //         return NotFound();
        //     }
        //
        //     var pmi = new PaymentMethodId(cryptoCode, PaymentTypes.LNURLPay);
        //     var lnpmi = new PaymentMethodId(cryptoCode, PaymentTypes.LightningLike);
        //     var methods = store.GetSupportedPaymentMethods(_btcPayNetworkProvider);
        //     var lnUrlMethod = methods.FirstOrDefault(method => method.PaymentId == pmi);
        //     var lnMethod = methods.FirstOrDefault(method => method.PaymentId == lnpmi);
        //     if (lnUrlMethod is null || lnMethod is null)
        //     {
        //         return NotFound();
        //     }
        //
        //     ViewPointOfSaleViewModel.Item[] items = new ViewPointOfSaleViewModel.Item[] { };
        //     string currencyCode;
        //     switch (app.AppType)
        //     {
        //         case nameof(AppType.Crowdfund):
        //             var cfS = app.GetSettings<CrowdfundSettings>();
        //             currencyCode = cfS.TargetCurrency;
        //             items = _appService.Parse(cfS.PerksTemplate, cfS.TargetCurrency);
        //             if (string.IsNullOrEmpty(itemCode))
        //             {
        //                 return NotFound();
        //             }
        //             break;
        //         case nameof(AppType.PointOfSale):
        //             var posS = app.GetSettings<AppsController.PointOfSaleSettings>();
        //             currencyCode = posS.Currency;
        //             items = _appService.Parse(posS.Template, posS.Currency);
        //             if (string.IsNullOrEmpty(itemCode) && !posS.ShowCustomAmount)
        //             {
        //                 return NotFound();
        //             }
        //             break;
        //     }
        //
        //     var item = items.FirstOrDefault(item1 =>
        //         item1.Id.Equals(itemCode, StringComparison.InvariantCultureIgnoreCase));
        //     if (!string.IsNullOrEmpty(itemCode) && item is null )
        //     {
        //         return NotFound();
        //     }
        //     else
        //     {
        //         return Ok(new LNURLPayRequest()
        //         {
        //             Tag = "payRequest",
        //             MinSendable = new LightMoney(1m, LightMoneyUnit.Satoshi),
        //             MaxSendable = LightMoney.FromUnit(6.12m, LightMoneyUnit.BTC),
        //             CommentAllowed = 0,
        //             Metadata = metadata,
        //             Callback = new Uri(Url.ActionLink(nameof(GetLNURLForInvoice), "LNURL",
        //                 new { cryptoCode, invoiceId = i.Id }, Request.Scheme, Request.Host.ToString(),
        //                 Request.PathBase))
        //         });
        //     }
        // }

        public class EditLightningAddressVM
        {
            public class EditLightningAddressItem : LightningAddressSettings.LightningAddressItem
            {
                [Required]
                [RegularExpression("[a-zA-Z0-9-_]+")]
                public string Username { get; set; }
            }

            public List<EditLightningAddressItem> Items { get; set; }
        }

        public class LightningAddressSettings
        {
            public class LightningAddressItem
            {
                public string StoreId { get; set; }
                public string CurrencyCode { get; set; } = null;
                public string CryptoCode { get; set; } = null;
                public decimal? Min { get; set; } = null;
                public decimal? Max { get; set; } = null;
            }

            public ConcurrentDictionary<string, LightningAddressItem> Items { get; set; } =
                new ConcurrentDictionary<string, LightningAddressItem>();

            public ConcurrentDictionary<string, string[]> StoreToItemMap { get; set; } =
                new ConcurrentDictionary<string, string[]>();
        }

        [HttpGet("~/.well-known/lnurlp/{username}")]
        public async Task<IActionResult> ResolveLightningAddress(string cryptoCode, string username)
        {
            if (!_lightningAddressSettings.Items.TryGetValue(username.ToLowerInvariant(), out var item))
            {
                return NotFound();
            }

            return await GetLNURL(item.CryptoCode, item.StoreId, item.CurrencyCode, item.Min, item.Max, username);
        }

        [HttpGet("pay")]
        public async Task<IActionResult> GetLNURL(string cryptoCode, string storeId, string currencyCode = null,
            decimal? min = null, decimal? max = null, string username = null)
        {
            currencyCode ??= cryptoCode;
            var network = _btcPayNetworkProvider.GetNetwork<BTCPayNetwork>(cryptoCode);
            if (network is null || !network.SupportLightning)
            {
                return NotFound();
            }

            var store = await _storeRepository.FindStore(storeId);
            if (store is null)
            {
                return NotFound();
            }

            var pmi = new PaymentMethodId(cryptoCode, PaymentTypes.LNURLPay);
            var lnpmi = new PaymentMethodId(cryptoCode, PaymentTypes.LightningLike);
            var methods = store.GetSupportedPaymentMethods(_btcPayNetworkProvider);
            var lnUrlMethod =
                methods.FirstOrDefault(method => method.PaymentId == pmi) as LNURLPaySupportedPaymentMethod;
            var lnMethod = methods.FirstOrDefault(method => method.PaymentId == lnpmi);
            if (lnUrlMethod is null || lnMethod is null)
            {
                return NotFound();
            }

            var blob = store.GetStoreBlob();
            if (blob.GetExcludedPaymentMethods().Match(pmi) || blob.GetExcludedPaymentMethods().Match(lnpmi) ||
                !blob.AnyoneCanInvoice)
            {
                return NotFound();
            }

            var lnAddress = username is null ? null : $"{username}@{Request.Host.ToString()}";
            List<string[]> lnurlMetadata = new List<string[]>();

            var i = await _invoiceController.CreateInvoiceCoreRaw(
                new CreateInvoiceRequest()
                {
                    Amount = null,
                    Checkout = new InvoiceDataBase.CheckoutOptions()
                    {
                        PaymentMethods = new[] { pmi.ToStringNormalized() },
                    },
                    Currency = currencyCode,
                    Type = InvoiceType.TopUp,
                }, store, Request.GetAbsoluteUri(""));

            if (!string.IsNullOrEmpty(username))
            {
                var pm = i.GetPaymentMethod(pmi);
                var paymentMethodDetails = (LNURLPayPaymentMethodDetails)pm.GetPaymentMethodDetails();
                paymentMethodDetails.ConsumedLightningAddress = lnAddress;
                await _invoiceRepository.NewPaymentDetails(i.Id, paymentMethodDetails, network);
            }

            lnurlMetadata.Add(new[] { "text/plain", i.Id });
            if (!string.IsNullOrEmpty(username))
            {
                lnurlMetadata.Add(new[] { "text/identifier", lnAddress });
            }

            return Ok(new LNURLPayRequest()
            {
                Tag = "payRequest",
                MinSendable = new LightMoney(min ?? 1m, LightMoneyUnit.Satoshi),
                MaxSendable = LightMoney.FromUnit(max ?? 6.12m, LightMoneyUnit.BTC),
                CommentAllowed = lnUrlMethod.LUD12Enabled ? 2000 : 0,
                Metadata = JsonConvert.SerializeObject(lnurlMetadata),
                Callback = new Uri(Url.ActionLink(nameof(GetLNURLForInvoice), "LNURL",
                    new { cryptoCode, invoiceId = i.Id }, Request.Scheme, Request.Host.ToString()))
            });
        }


        [HttpGet("pay/i/{invoiceId}")]
        public async Task<IActionResult> GetLNURLForInvoice(string invoiceId, string cryptoCode,
            [FromQuery] long? amount = null, string comment = null)
        {
            var network = _btcPayNetworkProvider.GetNetwork<BTCPayNetwork>(cryptoCode);
            if (network is null || !network.SupportLightning)
            {
                return NotFound();
            }

            var pmi = new PaymentMethodId(cryptoCode, PaymentTypes.LNURLPay);
            var i = await _invoiceRepository.GetInvoice(invoiceId, true);
            if (i.Status == InvoiceStatusLegacy.New)
            {
                var isTopup = i.IsUnsetTopUp();
                var lnurlSupportedPaymentMethod =
                    i.GetSupportedPaymentMethod<LNURLPaySupportedPaymentMethod>(pmi).FirstOrDefault();
                if (lnurlSupportedPaymentMethod is null ||
                    (!isTopup && !lnurlSupportedPaymentMethod.EnableForStandardInvoices))
                {
                    return NotFound();
                }

                var lightningPaymentMethod = i.GetPaymentMethod(pmi);
                var accounting = lightningPaymentMethod.Calculate();
                var paymentMethodDetails =
                    lightningPaymentMethod.GetPaymentMethodDetails() as LNURLPayPaymentMethodDetails;
                if (paymentMethodDetails.LightningSupportedPaymentMethod is null)
                {
                    return NotFound();
                }

                var min = new LightMoney(isTopup ? 1m : accounting.Due.ToUnit(MoneyUnit.Satoshi),
                    LightMoneyUnit.Satoshi);
                var max = isTopup ? LightMoney.FromUnit(6.12m, LightMoneyUnit.BTC) : min;

                List<string[]> lnurlMetadata = new List<string[]>();

                lnurlMetadata.Add(new[] { "text/plain", i.Id });
                if (!string.IsNullOrEmpty(paymentMethodDetails.ConsumedLightningAddress))
                {
                    lnurlMetadata.Add(new[] { "text/identifier", paymentMethodDetails.ConsumedLightningAddress });
                }

                var metadata = JsonConvert.SerializeObject(lnurlMetadata);
                if (amount.HasValue && (amount < min || amount > max))
                {
                    return BadRequest(new LNURL.LNUrlStatusResponse()
                    {
                        Status = "ERROR", Reason = "Amount is out of bounds."
                    });
                }

                if (amount.HasValue && string.IsNullOrEmpty(paymentMethodDetails.BOLT11) ||
                    paymentMethodDetails.GeneratedBoltAmount != amount)
                {
                    var client =
                        _lightningLikePaymentHandler.CreateLightningClient(
                            paymentMethodDetails.LightningSupportedPaymentMethod, network);
                    if (!string.IsNullOrEmpty(paymentMethodDetails.BOLT11))
                    {
                        try
                        {
                            await client.CancelInvoice(paymentMethodDetails.InvoiceId);
                        }
                        catch (Exception)
                        {
                            //not a fully supported option
                        }
                    }

                    var descriptionHash = new uint256(Hashes.SHA256(Encoding.UTF8.GetBytes(metadata)));
                    LightningInvoice invoice;
                    try
                    {
                        invoice = await client.CreateInvoice(new CreateInvoiceParams(amount.Value,
                            descriptionHash,
                            i.ExpirationTime.ToUniversalTime() - DateTimeOffset.UtcNow));
                        if (!BOLT11PaymentRequest.Parse(invoice.BOLT11, network.NBitcoinNetwork)
                            .VerifyDescriptionHash(metadata))
                        {
                            return BadRequest(new LNURL.LNUrlStatusResponse()
                            {
                                Status = "ERROR",
                                Reason = "Lightning node could not generate invoice with a VALID description hash"
                            });
                        }
                    }
                    catch (Exception e)
                    {
                        return BadRequest(new LNURL.LNUrlStatusResponse()
                        {
                            Status = "ERROR",
                            Reason = "Lightning node could not generate invoice with description hash"
                        });
                    }

                    paymentMethodDetails.BOLT11 = invoice.BOLT11;
                    paymentMethodDetails.InvoiceId = invoice.Id;
                    paymentMethodDetails.GeneratedBoltAmount = new LightMoney(amount.Value);
                    if (lnurlSupportedPaymentMethod.LUD12Enabled)
                    {
                        paymentMethodDetails.ProvidedComment = comment;
                    }

                    lightningPaymentMethod.SetPaymentMethodDetails(paymentMethodDetails);
                    await _invoiceRepository.UpdateInvoicePaymentMethod(invoiceId, lightningPaymentMethod);


                    _eventAggregator.Publish(new Events.InvoiceNewPaymentDetailsEvent(invoiceId,
                        paymentMethodDetails, pmi));
                    return Ok(new LNURLPayRequest.LNURLPayRequestCallbackResponse()
                    {
                        Disposable = true, Routes = Array.Empty<string>(), Pr = paymentMethodDetails.BOLT11
                    });
                }

                if (amount.HasValue && paymentMethodDetails.GeneratedBoltAmount == amount)
                {
                    if (lnurlSupportedPaymentMethod.LUD12Enabled && paymentMethodDetails.ProvidedComment != comment)
                    {
                        paymentMethodDetails.ProvidedComment = comment;
                        lightningPaymentMethod.SetPaymentMethodDetails(paymentMethodDetails);
                        await _invoiceRepository.UpdateInvoicePaymentMethod(invoiceId, lightningPaymentMethod);
                    }

                    return Ok(new LNURLPayRequest.LNURLPayRequestCallbackResponse()
                    {
                        Disposable = true, Routes = Array.Empty<string>(), Pr = paymentMethodDetails.BOLT11
                    });
                }

                if (amount is null)
                {
                    return Ok(new LNURL.LNURLPayRequest()
                    {
                        Tag = "payRequest",
                        MinSendable = min,
                        MaxSendable = max,
                        CommentAllowed = lnurlSupportedPaymentMethod.LUD12Enabled ? 2000 : 0,
                        Metadata = metadata,
                        Callback = new Uri(this.Request.GetCurrentUrl())
                    });
                }
            }

            return BadRequest(new LNURL.LNUrlStatusResponse()
            {
                Status = "ERROR", Reason = "Invoice not in a valid payable state"
            });
        }


        [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
        [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
        [HttpGet("~/stores/{storeId}/integrations/lightning-address")]
        public async Task<IActionResult> EditLightningAddress(string storeId)
        {
            var store = Request.HttpContext.GetStoreData();

            if (_lightningAddressSettings.StoreToItemMap.TryGetValue(storeId, out var addresses))
            {
                return View(new EditLightningAddressVM()
                {
                    Items = addresses.Select(s => new EditLightningAddressVM.EditLightningAddressItem()
                    {
                        Max = _lightningAddressSettings.Items[s].Max,
                        Min = _lightningAddressSettings.Items[s].Min,
                        CurrencyCode = _lightningAddressSettings.Items[s].CurrencyCode,
                        CryptoCode = _lightningAddressSettings.Items[s].CryptoCode,
                        StoreId = _lightningAddressSettings.Items[s].StoreId,
                        Username = s,
                    }).ToList()
                });
            }

            return View(new EditLightningAddressVM()
            {
                Items = new List<EditLightningAddressVM.EditLightningAddressItem>()
                {
                    new EditLightningAddressVM.EditLightningAddressItem() { }
                }
            });
        }

        [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
        [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
        [HttpPost("~/stores/{storeId}/integrations/lightning-address")]
        public async Task<IActionResult> EditLightningAddress(string storeId, [FromForm] EditLightningAddressVM vm,
            string command)
        {
            if (command.StartsWith("remove", StringComparison.InvariantCultureIgnoreCase))
            {
                ModelState.Clear();
                var index = int.Parse(
                    command.Substring(command.IndexOf(":", StringComparison.InvariantCultureIgnoreCase) + 1),
                    CultureInfo.InvariantCulture);
                vm.Items.RemoveAt(index);
                return View(vm);
            }

            if (command == "add")
            {
                vm.Items.Add(new EditLightningAddressVM.EditLightningAddressItem());
                return View(vm);
            }

            if (vm.Items?.Any() is true)
            {
                for (var i = 0; i < vm.Items.Count; i++)
                {
                    if (_lightningAddressSettings.Items.TryGetValue(vm.Items[i].Username.ToLowerInvariant(),
                        out var existing) && existing.StoreId != storeId)
                    {
                        ModelState.AddModelError(dictionary => vm.Items[i].Username, "Username is already taken", this);
                    }
                }
            }

            if (!ModelState.IsValid)
            {
                return View(vm);
            }

            if (command == "save")
            {
                var ids = vm.Items.Select(item => item.Username.ToLowerInvariant()).ToArray();
                _lightningAddressSettings.StoreToItemMap.AddOrReplace(storeId, ids);
                foreach (var lightningAddressItem in vm.Items)
                {
                    lightningAddressItem.StoreId = storeId;
                    lightningAddressItem.CryptoCode = "BTC";
                    _lightningAddressSettings.Items.AddOrReplace(lightningAddressItem.Username.ToLowerInvariant(),
                        lightningAddressItem);
                }
            }

            await _settingsRepository.UpdateSetting(_lightningAddressSettings);
            TempData.SetStatusMessageModel(new StatusMessageModel()
            {
                Message = "Saved Lightning addresses successfully."
            });

            return RedirectToAction("EditLightningAddress");
        }
    }
}
