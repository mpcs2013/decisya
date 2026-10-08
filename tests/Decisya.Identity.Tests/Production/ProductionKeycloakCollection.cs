namespace Decisya.Identity.Tests;

/// <summary>
/// One <see cref="ProductionKeycloak"/> (Postgres plus Keycloak, started through the real wrapper)
/// shared by every production-realm Integration class of this project. Tests in one collection run
/// one after another, so a test that adds or removes a user for the identity check can not race
/// another that counts them.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ProductionKeycloakDefinition : ICollectionFixture<ProductionKeycloak>
{
    public const string Name = "ProductionKeycloak";
}
