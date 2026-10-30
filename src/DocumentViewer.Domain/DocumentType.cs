using System.Text.Json.Serialization;

namespace DocumentViewer.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DocumentType
{
    Unknown = 0,
    PurchaseOrder,
    FinanceAgreement,
    RegistrationDocument,
    JobCard,
    InspectionReport,
    WarrantyClaim
}
