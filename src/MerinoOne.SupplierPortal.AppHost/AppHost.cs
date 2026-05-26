var builder = DistributedApplication.CreateBuilder(args);

// Service name "supplierPortal-api" matches SupplierPortal:ApiBaseUrl = "https+http://supplierPortal-api"
// so service discovery in the Web project resolves to the API endpoint at runtime.
var api = builder.AddProject("supplierPortal-api", "../MerinoOne.SupplierPortal/MerinoOne.SupplierPortal.csproj");

builder
	.AddProject("supplierPortal-web", "../MerinoOne.Web/MerinoOne.Web.csproj")
	.WithReference(api)
	.WaitFor(api);

builder.Build().Run();
