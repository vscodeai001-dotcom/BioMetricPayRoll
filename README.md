# 1000 Web Patch

Replace the existing Web file at:

`Web/Payroll.Web/Services/FirebaseRealtimeService.cs`

with:

`WebPatch/Payroll.Web/Services/FirebaseRealtimeService.cs`

The only Leave Management behavior change in this file is the Firebase status mapping for `LeaveRequest`: `IsApproved=false` is represented as `Pending`, matching the existing Web service. No database schema or leave calculation logic is changed.
