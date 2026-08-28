using System.Diagnostics.CodeAnalysis;

// ReSharper disable MemberHidesStaticFromOuterClass

namespace Defra.WasteObligations.Testing;

[SuppressMessage(
    "Critical Code Smell",
    "S3218:Inner class members should not shadow outer class \"static\" or type members"
)]
public static class Endpoints
{
    public static class Health
    {
        public static string Ready() => "health";

        public static string Authorized() => $"{Ready()}/authorized";

        public static string All() => $"{Ready()}/all";
    }

    public static class OpenApi
    {
        public const string V1 = "documentation/openapi/v1.json";
    }

    public static class Organisations
    {
        private static string Root => "organisations";

        public static string Read(Guid id) => $"{Root}/{id}";

        public static class Obligations
        {
            private static string Root = "obligations";

            public static string Read(Guid organisationId, EndpointQuery? query = null) =>
                $"{Organisations.Read(organisationId)}/{Root}{query}";
        }

        public static class Prns
        {
            private static string Root = "prns";

            public static string Search(Guid organisationId, EndpointQuery? query = null) =>
                $"{Organisations.Read(organisationId)}/{Root}{query}";

            public static string Read(Guid organisationId, string prnId) =>
                $"{Organisations.Read(organisationId)}/{Root}/{prnId}";

            public static string Update(Guid organisationId, string prnId) => Read(organisationId, prnId);
        }

        public static class ComplianceDeclarations
        {
            private static string Root = "compliance-declarations";

            public static string Create(Guid organisationId) => $"{Organisations.Read(organisationId)}/{Root}";

            public static string Read(Guid organisationId, EndpointQuery? query = null) =>
                $"{Create(organisationId)}{query}";

            public static string Read(Guid organisationId, string complianceDeclarationId) =>
                $"{Create(organisationId)}/{complianceDeclarationId}";

            public static string Update(Guid organisationId, string complianceDeclarationId) =>
                $"{Read(organisationId, complianceDeclarationId)}";
        }
    }

    public static class ComplianceDeclarations
    {
        private static string Root = "compliance-declarations";

        public static string Search(EndpointQuery? query = null) => $"{Root}{query}";

        public static string Unsubmitted(EndpointQuery? query = null) => $"{Root}/unsubmitted{query}";

        public static string Delete(string id) => $"{Root}/{id}";
    }
}
