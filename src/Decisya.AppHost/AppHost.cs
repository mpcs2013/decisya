var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Decisya_Api>("decisya-api");

builder.Build().Run();
