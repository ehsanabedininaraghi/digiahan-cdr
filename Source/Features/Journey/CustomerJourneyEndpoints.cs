using DigiAhan.CDR.Receiver.Models;
using DigiAhan.CDR.Receiver.Services;

namespace DigiAhan.CDR.Receiver.Features.Journey;

public static class CustomerJourneyEndpoints
{
    public static IEndpointRouteBuilder MapCustomerJourneyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/seller-v3", () => Results.Redirect("/seller-v3/index.html"));
        endpoints.MapGet("/journey-control", () => Results.Redirect("/journey-control/index.html"));

        endpoints.MapGet("/api/seller-v3/status", async (
            HttpContext context,
            SellerWorkspaceAccessService access,
            CustomerJourneyRepository journey,
            CancellationToken ct) =>
        {
            var seller = await access.AuthenticateAsync(context, ct);
            if (seller is null) return Results.Unauthorized();
            return Results.Ok(new
            {
                enabled = journey.IsEnabledFor(seller),
                version = "4.4.0",
                mode = journey.IsEnabledFor(seller) ? "PILOT" : "DISABLED"
            });
        });

        endpoints.MapGet("/api/seller-v3/workspace", async (
            HttpContext context,
            int? take,
            SellerWorkspaceAccessService access,
            CustomerJourneyRepository journey,
            CancellationToken ct) =>
        {
            var seller = await access.AuthenticateAsync(context, ct);
            if (seller is null) return Results.Unauthorized();
            if (!journey.IsEnabledFor(seller)) return FeatureDisabled();
            return Results.Ok(await journey.GetWorkspaceAsync(seller, take ?? 40, ct));
        });

        endpoints.MapPost("/api/seller-v3/leads", async (
            HttpContext context,
            JourneyCreateLeadRequest request,
            SellerWorkspaceAccessService access,
            CustomerJourneyRepository journey,
            CancellationToken ct) =>
        {
            var seller = await access.AuthenticateAsync(context, ct);
            if (seller is null) return Results.Unauthorized();
            if (!journey.IsEnabledFor(seller)) return FeatureDisabled();
            try
            {
                return Results.Ok(await journey.CreateLeadAsync(seller, request, ct));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                return JourneyError(ex);
            }
        });

        endpoints.MapPost("/api/seller-v3/leads/{leadId:long}/qualify", async (
            long leadId,
            HttpContext context,
            JourneyQualifyLeadRequest request,
            SellerWorkspaceAccessService access,
            CustomerJourneyRepository journey,
            CancellationToken ct) =>
        {
            var seller = await access.AuthenticateAsync(context, ct);
            if (seller is null) return Results.Unauthorized();
            if (!journey.IsEnabledFor(seller)) return FeatureDisabled();
            try
            {
                return Results.Ok(await journey.QualifyLeadAsync(seller, leadId, request, ct));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                return JourneyError(ex);
            }
        });

        endpoints.MapPost("/api/seller-v3/opportunities/{opportunityId:long}/stage", async (
            long opportunityId,
            HttpContext context,
            JourneyTransitionOpportunityRequest request,
            SellerWorkspaceAccessService access,
            CustomerJourneyRepository journey,
            CancellationToken ct) =>
        {
            var seller = await access.AuthenticateAsync(context, ct);
            if (seller is null) return Results.Unauthorized();
            if (!journey.IsEnabledFor(seller)) return FeatureDisabled();
            try
            {
                return Results.Ok(await journey.TransitionOpportunityAsync(seller, opportunityId, request, ct));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                return JourneyError(ex);
            }
        });

        endpoints.MapPost("/api/seller-v3/work-items/{workItemId:long}/complete", async (
            long workItemId,
            HttpContext context,
            JourneyCompleteWorkItemRequest request,
            SellerWorkspaceAccessService access,
            CustomerJourneyRepository journey,
            CancellationToken ct) =>
        {
            var seller = await access.AuthenticateAsync(context, ct);
            if (seller is null) return Results.Unauthorized();
            if (!journey.IsEnabledFor(seller)) return FeatureDisabled();
            try
            {
                return Results.Ok(await journey.CompleteWorkItemAsync(seller, workItemId, request, ct));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                return JourneyError(ex);
            }
        });

        endpoints.MapGet("/api/journey-control/exceptions", async (
            int? take,
            CustomerJourneyRepository journey,
            CancellationToken ct) =>
        {
            if (!journey.IsEnabled) return FeatureDisabled();
            return Results.Ok(await journey.GetManagerExceptionsAsync(take ?? 200, ct));
        });

        return endpoints;
    }

    private static IResult FeatureDisabled()
        => Results.NotFound(new
        {
            error = "نسخه آزمایشی سفر مشتری برای این کاربر فعال نشده است.",
            code = "JOURNEY_FEATURE_DISABLED"
        });

    private static IResult JourneyError(Exception exception)
    {
        var code = exception.Message;
        var message = code switch
        {
            "IDEMPOTENCY_KEY_INVALID" => "شناسه یکتای درخواست معتبر نیست؛ صفحه را تازه‌سازی کنید.",
            "IDENTITY_REQUIRED" or "IDENTITY_NOT_FOUND" => "ابتدا یک مشتری معتبر را انتخاب کنید.",
            "LEAD_TITLE_INVALID" => "عنوان سرنخ معتبر نیست.",
            "OPPORTUNITY_TITLE_INVALID" => "عنوان فرصت فروش معتبر نیست.",
            "NEXT_ACTION_INVALID" => "اقدام بعدی را مشخص کنید.",
            "NEXT_ACTION_TIME_INVALID" => "زمان اقدام بعدی معتبر نیست.",
            "OPPORTUNITY_STAGE_INVALID" => "مرحله فرصت فروش معتبر نیست.",
            "WORK_ITEM_OUTCOME_INVALID" => "نتیجه کار معتبر نیست.",
            "LOST_REASON_REQUIRED" => "برای فرصت از دست رفته، دلیل را ثبت کنید.",
            "LEAD_NOT_OPEN" => "این سرنخ قبلاً تبدیل یا بسته شده است.",
            "OPPORTUNITY_VALUE_INVALID" => "مقدار یا مبلغ فرصت فروش معتبر نیست.",
            "LEAD_NOT_FOUND" => "سرنخ پیدا نشد یا متعلق به این فروشنده نیست.",
            "OPPORTUNITY_NOT_FOUND" => "فرصت فروش پیدا نشد یا متعلق به این فروشنده نیست.",
            "WORK_ITEM_NOT_FOUND" => "کار پیدا نشد، قبلاً بسته شده یا متعلق به این فروشنده نیست.",
            _ => "اطلاعات سفر مشتری معتبر نیست."
        };
        return exception is KeyNotFoundException
            ? Results.NotFound(new { error = message, code })
            : Results.BadRequest(new { error = message, code });
    }
}
