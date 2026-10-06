namespace AiFramework.Application.Orders;

/// <summary>Draws an export as a file. Implemented in Infrastructure, which alone knows the PDF library.</summary>
public interface IOrderExportRenderer
{
    public byte[] Render(OrderExportReport report);
}
