using System.ComponentModel;

namespace Zapret.Gui.Services;

public sealed partial class FlowsealAdapter
{
    private sealed class EarlyProcessExitException(string message) : IOException(message);

    // Explanation is for the user; original exception messages belong in ApplyError.
    private static string ExplainFailure(Exception error) => error switch
    {
        EarlyProcessExitException => "Запущенный процесс завершился раньше времени. Попробуйте другую исходную стратегию после проверки состояния службы.",
        ServiceConflictException => "Служба, процесс или файлы изменились извне. Изменение остановлено, чтобы сохранить внешние правки.",
        UnauthorizedAccessException or System.Security.SecurityException => "Недостаточно прав доступа. Проверьте права на поставку и управление службой Windows.",
        Win32Exception { NativeErrorCode: 5 or 740 or 1314 } => "Windows отказала в доступе. Для управления службой нужны права администратора.",
        TimeoutException => "Windows или запущенный процесс не подтвердили ожидаемое состояние вовремя. Обновите состояние перед повтором.",
        Win32Exception => "Windows не смогла выполнить операцию со службой или процессом. Обновите состояние и проверьте права доступа.",
        IOException => "Не удалось завершить работу с файлами или процессом. Проверьте доступность поставки и обновите состояние.",
        _ => "Данные поставки или состояние операции не удалось проверить. Проверьте исходные файлы и обновите состояние."
    };
}
