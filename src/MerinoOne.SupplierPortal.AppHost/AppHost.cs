var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject("api", "../MerinoOne.SupplierPortal/MerinoOne.SupplierPortal.csproj");

builder
	.AddProject("web", "../MerinoOne.Web/MerinoOne.Web.csproj")
	.WithReference(api);

builder.Build().Run();
