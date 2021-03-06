﻿using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using RenewalWebsite.Models;
using Stripe;

namespace RenewalWebsite.Services
{
    public interface IDonationService
    {
        Dictionary<PaymentCycle, string> GetCycles();
        void Save(Donation donation);
        Donation GetById(int id);
        StripePlan GetOrCreatePlan(Donation donation);
        int GetByUserId(string userId);
    }
}