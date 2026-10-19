using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Hisui.Pdf.Core;

/// <summary>
/// DI registration for the PDF engine. The host (WPF app or tests) calls <see cref="AddPdfCore"/> to
/// pull in every service implementation. Implementations are added here as each feature phase lands.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPdfCore(this IServiceCollection services)
    {
        services.AddSingleton<PdfiumRenderer>();
        services.AddSingleton<IPdfRenderer>(sp => sp.GetRequiredService<PdfiumRenderer>());
        services.AddSingleton<IPdfiumDocumentHost>(sp => sp.GetRequiredService<PdfiumRenderer>());
        services.AddSingleton<IPdfPageService, PdfPageService>();
        services.AddSingleton<IPdfAnnotationService, PdfAnnotationService>();
        services.AddSingleton<IPdfRedactionService, PdfRedactionService>();
        services.AddSingleton<IPdfFormService, PdfFormService>();
        services.AddSingleton<IPdfFormDetector, PdfFormDetector>();
        services.AddSingleton<IPdfCommentService, PdfCommentService>();
        services.AddSingleton<IPdfNavigationService, PdfNavigationService>();
        services.AddSingleton<IPdfCompareService, PdfCompareService>();
        services.AddSingleton<IPdfConformanceService, PdfConformanceService>();
        services.AddSingleton<IPdfOfficeExporter, PdfOfficeExporter>();
        services.AddSingleton<IPdfSignatureService>(_ => new PdfSignatureService(new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) }));
        services.AddSingleton<IPdfTextExtractor, PdfTextExtractor>();
        services.AddSingleton<IPdfImageExtractor, PdfImageExtractor>();
        services.AddSingleton<IPdfWordExporter, PdfWordExporter>();
        services.AddSingleton<IPdfSecurityService, PdfSecurityService>();
        services.AddSingleton<IPdfMetadataService, PdfMetadataService>();
        services.AddSingleton<IPdfOptimizer, PdfOptimizer>();
        services.AddSingleton<IPdfTextEditService, PdfTextEditService>();
        services.AddSingleton<IOcrEngine, TesseractOcrEngine>();
        services.AddSingleton<IPdfOcrService, PdfOcrService>();
        return services;
    }
}
