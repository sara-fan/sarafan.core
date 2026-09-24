// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging.Abstractions;

using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;
using Sarafan.Core.Models;
using Sarafan.Core.Authentication;

namespace Sarafan.Core.Services;

public sealed class SarafanProblemDetailsFactory(
    ILogger<SarafanProblemDetailsFactory>? logger = null)
{
    private readonly ILogger<SarafanProblemDetailsFactory> _logger = logger ?? NullLogger<SarafanProblemDetailsFactory>.Instance;

    public const string MediaType = "application/problem+json";
    public const string TypeBase = "https://sarafan.sw.consulting/problems/";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly IReadOnlyDictionary<string, ProblemDefinition> Definitions =
        new Dictionary<string, ProblemDefinition>(StringComparer.Ordinal)
        {
            ["invalid_service_catalogue_service"] = new(400, "Некорректная услуга", "Выберите услугу из каталога."),
            ["service_catalogue_product_reserved"] = new(409, "Базовый тариф товара защищён", "Базовая запись стоимости товара неизменяема. Её нельзя создать повторно, изменить или удалить."),
            ["invalid_service_catalogue_method"] = new(400, "Некорректный способ расчёта", "Выберите способ расчёта из каталога."),
            ["invalid_service_catalogue_currency"] = new(400, "Некорректная валюта тарифа", "Для тарифа доступны только российский рубль и доллар США."),
            ["invalid_service_catalogue_percentage"] = new(400, "Некорректный процент", "Укажите процент больше нуля и не больше 100, максимум с четырьмя дробными знаками."),
            ["invalid_service_catalogue_amount"] = new(400, "Некорректная сумма", "Проверьте сумму и параметры выбранного способа расчёта."),
            ["invalid_service_catalogue_minimum_amount"] = new(400, "Некорректная минимальная сумма", "Укажите неотрицательную сумму в валюте тарифа, максимум с двумя дробными знаками."),
            ["invalid_service_catalogue_bands"] = new(400, "Некорректные интервалы", "Проверьте границы интервалов и суммы ступенчатого тарифа."),
            ["invalid_order_pricing"] = new(400, "Некорректные параметры расчёта", "Проверьте услуги и разрешённые ручные суммы."),
            ["order_pricing_unavailable"] = new(409, "Расчёт не готов", "Сохраните полный расчёт перед подтверждением. Проверьте тарифы, курс и значения компонентов."),
            ["invalid_service_catalogue_maximum_amount"] = new(400, "Некорректная максимальная сумма", "Укажите сумму не меньше минимума, максимум с двумя дробными знаками."),
            ["invalid_service_catalogue_dates"] = new(400, "Некорректный период действия", "Проверьте даты начала и окончания действия тарифа."),
            ["invalid_service_catalogue_version"] = new(400, "Не указана версия тарифа", "Обновите карточку и повторите действие с текущей версией тарифа."),
            ["invalid_service_catalogue_audit_filter"] = new(400, "Некорректные параметры журнала тарифов", "Проверьте страницу, размер страницы, поиск, услугу, действие и сортировку."),
            ["service_catalogue_entry_not_found"] = new(404, "Тариф не найден", "Запрошенная запись каталога услуг не найдена."),
            ["service_catalogue_period_overlap"] = new(409, "Периоды тарифов пересекаются", "Для одной услуги периоды действия тарифов не должны пересекаться, включая граничные даты."),
            ["service_catalogue_update_conflict"] = new(409, "Тариф изменился", "Обновите карточку и повторите изменения с текущей версией тарифа."),
            ["invalid_store_sort"] = new(400, "Некорректная сортировка магазинов", "Выберите рекомендуемый порядок или сортировку по названию."),
            ["invalid_store_search"] = new(400, "Некорректный поиск магазинов", "Сократите строку поиска до 200 символов."),
            ["invalid_store_name"] = new(400, "Некорректное название магазина", "Укажите название магазина длиной от 1 до 200 символов."),
            ["invalid_store_description"] = new(400, "Некорректное описание магазина", "Укажите описание длиной от 1 до 160 символов."),
            ["invalid_store_url"] = new(400, "Некорректная ссылка магазина", "Укажите адрес HTTP(S) с допустимым доменом верхнего уровня, без логина и пароля."),
            ["invalid_store_status"] = new(400, "Некорректный статус магазина", "Выберите статус магазина из списка."),
            ["store_display_order_conflict"] = new(409, "Порядок показа уже занят", "Этот номер порядка показа уже используется другим магазином."),
            ["store_priority_limit_exceeded"] = new(409, "Достигнут лимит магазинов на главной", "На главной странице можно показывать не более шести магазинов."),
            ["invalid_store_display_order"] = new(400, "Некорректный порядок магазина", "Укажите целое число не меньше нуля."),
            ["invalid_store_version"] = new(400, "Не указана версия магазина", "Обновите карточку и повторите действие с текущей версией магазина."),
            ["store_update_conflict"] = new(409, "Магазин изменился", "Обновите карточку и повторите изменения."),
            ["store_logo_required"] = new(400, "Требуется изображение магазина", "Загрузите изображение магазина или сформируйте его из названия."),
            ["invalid_store_logo_size"] = new(400, "Некорректный размер изображения магазина", "Загрузите непустое изображение размером не больше 2048 Кб."),
            ["invalid_store_logo_type"] = new(400, "Неподдерживаемый формат изображения магазина", "Загрузите изображение в формате PNG, JPEG или WebP."),
            ["invalid_store_logo_content"] = new(400, "Некорректное содержимое изображения магазина", $"Загрузите корректный статичный PNG, JPEG или WebP: не более {StoreImageContent.MaxDimension} пикселей по стороне и {StoreImageContent.MaxPixels} пикселей всего. Для анимации WebP допустимо до {StoreImageContent.MaxFrames} кадров и {StoreImageContent.MaxAnimationPixels} пикселей холста суммарно; для метаданных PNG — до {StoreImageContent.MaxMetadataBytes} распакованных байт."),
            ["order_quantity_limit_exceeded"] = new(400, "Превышено количество товара", "Такое количество товара может быть признано коммерческой партией и запрещено к ввозу"),
            ["order_value_limit_exceeded"] = new(400, "Превышена стоимость заказа", OrderLimitService.ExceededMessage),
            ["order_limit_rates_unavailable"] = new(503, "Курсы временно недоступны", "Не удалось проверить стоимость. Повторите попытку позже."),
            ["order_update_conflict"] = new(409, "Заказ изменился", "Обновите карточку и повторите изменения."),
            ["order_not_editable"] = new(409, "Товар недоступен для редактирования", "Изменять товар можно только во время проверки заказа."),
            ["invalid_order_product_name"] = new(400, "Некорректное название товара", "Укажите название товара длиной от 1 до 500 символов."),
            ["invalid_order_store_name"] = new(400, "Некорректное название магазина", "Название магазина не должно превышать 200 символов."),
            ["invalid_order_seller_price"] = new(400, "Некорректная цена товара", "Укажите положительную цену в USD не более 99999999,99, максимум с двумя дробными знаками."),
            ["invalid_order_color"] = new(400, "Некорректный цвет товара", "Цвет не должен превышать 200 символов."),
            ["invalid_order_size"] = new(400, "Некорректный размер товара", "Размер не должен превышать 200 символов."),
            ["invalid_legal_document_kind"] = new(400, "Некорректный вид документа", "Выберите вид документа из предложенного списка."),
            ["invalid_legal_document_locale"] = new(400, "Некорректный язык документа", "Для документа укажите язык ru."),
            ["invalid_legal_document_title"] = new(400, "Некорректное название документа", "Укажите непустое название длиной не более 200 символов."),
            ["invalid_legal_document_version"] = new(400, "Некорректная версия документа", "Укажите версию длиной не более 64 символов."),
            ["invalid_legal_document_audit_filter"] = new(400, "Некорректный фильтр журнала", "Выберите действие создания или удаления и сократите строку поиска до 200 символов."),
            ["legal_document_file_required"] = new(400, "Файл документа не загружен", "Загрузите непустой файл Markdown с расширением .md."),
            ["legal_document_file_too_large"] = new(400, "Файл документа слишком большой", "Размер файла не должен превышать 256 Кб."),
            ["legal_document_file_type"] = new(400, "Некорректный тип файла", "Загрузите файл Markdown с расширением .md."),
            ["legal_document_encoding"] = new(400, "Некорректная кодировка файла", "Сохраните файл в кодировке UTF-8 и загрузите его снова."),
            ["legal_document_text_required"] = new(400, "Документ пуст", "Добавьте текст в документ и загрузите файл снова."),
            ["legal_document_control_character"] = new(400, "Недопустимый символ в документе", "Удалите управляющие символы из текста документа."),
            ["legal_document_html_not_allowed"] = new(400, "HTML в документе не поддерживается", "Удалите HTML-разметку из документа."),
            ["legal_document_code_not_allowed"] = new(400, "Код в документе не поддерживается", "Удалите строчный или блочный код из документа."),
            ["legal_document_quote_not_allowed"] = new(400, "Цитатный блок не поддерживается", "Удалите цитатный блок из документа."),
            ["legal_document_separator_not_allowed"] = new(400, "Разделитель не поддерживается", "Удалите горизонтальный разделитель Markdown из документа."),
            ["legal_document_image_not_allowed"] = new(400, "Изображения не поддерживаются", "Удалите изображение из документа."),
            ["legal_document_link_not_allowed"] = new(400, "Некорректная ссылка в документе", "Используйте абсолютную ссылку с протоколом http, https или mailto."),
            ["legal_document_not_found"] = new(404, "Документ не найден", "Документ отсутствует или дата начала его действия ещё не наступила."),
            ["invalid_effective_date"] = new(400, "Некорректная дата начала действия", "Выберите сегодняшнюю или будущую дату по московскому времени."),
            ["legal_document_effective_date_conflict"] = new(409, "Дата уже используется", "Для этого вида документа уже существует версия с выбранной датой начала действия."),
            ["legal_document_version_conflict"] = new(409, "Версия уже используется", "Для этого вида документа уже существует запись с таким обозначением версии."),
            ["legal_document_already_effective"] = new(409, "Документ уже действует", "Удалить документ можно только до даты начала его действия."),
            ["consent_conflict"] = new(409, "Данные изменились", "Обновите данные и повторите действие с актуальными сведениями."),
            ["consent_document_unavailable"] = new(409, "Текст согласия недоступен", "Действующий документ пока недоступен. Повторите позже; чтение и реализация прав остаются доступны."),
            ["consent_version_changed"] = new(409, "Версия согласия изменилась", "Откройте действующий документ и подтвердите согласие заново."),
            ["personal_data_consent_required"] = new(409, "Требуется актуальное согласие", "Для этой операции откройте раздел «Согласия» и примите действующую версию согласия на обработку персональных данных."),
            ["invalid_consent_decision"] = new(400, "Некорректное решение", "Проверьте версию документа и выбранное решение."),
            ["onboarding_consent_expired"] = new(400, "Подтверждение согласия истекло", "Вернитесь к вводу телефона и подтвердите актуальный текст согласия."),
            ["consent_withdrawal_request_not_found"] = new(404, "Запрос не найден", "Обновите очередь: выбранный запрос отсутствует."),
            ["invalid_consent_withdrawal_request_filter"] = new(400, "Некорректные параметры очереди", "Проверьте страницу, размер страницы, поиск, статус и сортировку."),
            ["validation_failed"] = new(
                StatusCodes.Status400BadRequest,
                "Ошибка проверки данных",
                "Проверьте корректность указанных данных."),
            ["invalid_phone"] = new(
                StatusCodes.Status400BadRequest,
                "Некорректный номер телефона",
                "Введите 11 цифр, начиная с 8 или +7. Например: +7 (921) 123-45-67."),
            ["invalid_auth_request"] = new(
                StatusCodes.Status400BadRequest,
                "Некорректный запрос входа",
                "Отправьте только те подтверждения, которые требуются для текущего шага входа."),
            ["authentication_requirements_changed"] = new(
                StatusCodes.Status409Conflict,
                "Требования входа изменились",
                "Вернитесь к номеру телефона и продолжите вход с актуальными требованиями."),
            ["consent_required"] = new(
                StatusCodes.Status400BadRequest,
                "Требуется согласиться с документами",
                "Необходимо принять условия предоставления сервиса и согласие на хранение и обработку персональных данных."),
            ["invalid_photo_size"] = new(
                StatusCodes.Status400BadRequest,
                "Некорректный размер фотографии",
                "Размер фотографии должен быть от 1 байта до 5 МиБ."),
            ["invalid_photo_type"] = new(
                StatusCodes.Status400BadRequest,
                "Неподдерживаемый формат фотографии",
                "Используйте фотографию в формате JPEG, PNG или WebP."),
            ["invalid_photo_content"] = new(
                StatusCodes.Status400BadRequest,
                "Некорректное содержимое фотографии",
                "Содержимое фотографии не соответствует указанному формату."),
            ["invalid_order_url"] = new(
                StatusCodes.Status400BadRequest,
                "Некорректная ссылка на товар",
                "Проверьте ссылку на товар и попробуйте ещё раз"),
            ["tld_catalog_unavailable"] = new(
                StatusCodes.Status503ServiceUnavailable,
                "Каталог доменов временно недоступен",
                "Проверка ссылки временно недоступна. Повторите попытку позже."),
            ["invalid_order_quantity"] = new(
                StatusCodes.Status400BadRequest,
                "Некорректное количество товара",
                "Укажите положительное количество товара."),
            ["invalid_order_comment"] = new(
                StatusCodes.Status400BadRequest,
                "Некорректный комментарий",
                "Комментарий не должен превышать 2000 символов."),
            ["invalid_order_list_filter"] = new(
                StatusCodes.Status400BadRequest,
                "Некорректные параметры списка заказов",
                "Проверьте страницу, размер страницы, поиск, статус, даты и сортировку."),
            ["invalid_order_idempotency_key"] = new(
                StatusCodes.Status400BadRequest,
                "Некорректный ключ запроса",
                "Передайте непустой UUID в заголовке Idempotency-Key."),
            ["order_creation_conflict"] = new(
                StatusCodes.Status409Conflict,
                "Запрос создания заказа изменён",
                "Повторите запрос с исходными данными заказа или новым ключом Idempotency-Key."),
            ["order_number_allocation_failed"] = new(
                StatusCodes.Status503ServiceUnavailable,
                "Не удалось создать номер заказа",
                "Повторите попытку создания заказа позже."),
            ["invalid_code"] = new(
                StatusCodes.Status401Unauthorized,
                "Некорректный код подтверждения",
                "Код подтверждения неверен."),
            ["invalid_access_token"] = new(
                StatusCodes.Status401Unauthorized,
                "Недействительный токен доступа",
                "Войдите в систему повторно."),
            ["invalid_refresh_token"] = new(
                StatusCodes.Status401Unauthorized,
                "Сеанс завершён",
                "Сеанс истёк или больше недействителен. Войдите в систему повторно."),
            ["invalid_backoffice_access_token"] = new(
                StatusCodes.Status401Unauthorized,
                "Недействительный токен сотрудника",
                "Войдите в служебную систему повторно."),
            ["invalid_backoffice_refresh_token"] = new(
                StatusCodes.Status401Unauthorized,
                "Служебный сеанс завершён",
                "Служебный сеанс истёк или больше недействителен. Войдите повторно."),
            ["login_failed"] = new(
                StatusCodes.Status401Unauthorized,
                "Не удалось войти",
                "Номер телефона или код подтверждения неверен."),
            ["backoffice_login_failed"] = new(
                StatusCodes.Status401Unauthorized,
                "Не удалось войти",
                "Адрес электронной почты или пароль неверен."),
            ["access_denied"] = new(
                StatusCodes.Status403Forbidden,
                "Доступ запрещён",
                "У вас нет прав для выполнения этого действия."),
            ["customer_not_found"] = new(
                StatusCodes.Status404NotFound,
                "Пользователь не найден",
                "Запрошенный пользователь не найден."),
            ["photo_not_found"] = new(
                StatusCodes.Status404NotFound,
                "Фотография не найдена",
                "Фотография пользователя ещё не загружена."),
            ["backoffice_user_not_found"] = new(
                StatusCodes.Status404NotFound,
                "Сотрудник не найден",
                "Запрошенная служебная учётная запись не найдена."),
            ["account_exists"] = new(
                StatusCodes.Status409Conflict,
                "Учётная запись уже существует",
                "Для этого номера телефона уже зарегистрирована учётная запись."),
            ["backoffice_email_exists"] = new(
                StatusCodes.Status409Conflict,
                "Учётная запись уже существует",
                "Служебная учётная запись с этим адресом электронной почты уже существует."),
            ["last_backoffice_administrator"] = new(
                StatusCodes.Status409Conflict,
                "Требуется администратор",
                "Нельзя отключить или понизить последнего активного администратора."),
            ["demo_backoffice_forbidden"] = new(
                StatusCodes.Status409Conflict,
                "Демонстрационная учётная запись запрещена",
                "Сначала замените демонстрационный пароль или отключите эту учётную запись."),
            ["invalid_backoffice_role"] = new(
                StatusCodes.Status400BadRequest,
                "Некорректная служебная роль",
                "Укажите хотя бы одну роль из доступного каталога."),
            ["invalid_backoffice_user_data"] = new(
                StatusCodes.Status400BadRequest,
                "Некорректные данные сотрудника",
                "Проверьте имя, фамилию и требования к паролю."),
            ["rate_limited"] = new(
                StatusCodes.Status429TooManyRequests,
                "Слишком много запросов",
                "Повторите попытку позже."),
            ["internal_error"] = new(
                StatusCodes.Status500InternalServerError,
                "Внутренняя ошибка",
                "Не удалось обработать запрос. Повторите попытку позже."),
            ["verification_unavailable"] = new(
                StatusCodes.Status503ServiceUnavailable,
                "Подтверждение временно недоступно",
                "Сервис подтверждения телефона временно недоступен."),
            ["resource_not_found"] = new(
                StatusCodes.Status404NotFound,
                "Ресурс не найден",
                "Запрошенный ресурс не найден."),
            ["method_not_allowed"] = new(
                StatusCodes.Status405MethodNotAllowed,
                "Метод не поддерживается",
                "Этот метод нельзя использовать для запрошенного ресурса."),
            ["request_too_large"] = new(
                StatusCodes.Status413PayloadTooLarge,
                "Запрос слишком большой",
                "Уменьшите размер отправляемых данных."),
            ["unsupported_media_type"] = new(
                StatusCodes.Status415UnsupportedMediaType,
                "Неподдерживаемый формат данных",
                "Используйте поддерживаемый формат данных запроса."),
            ["bad_request"] = new(
                StatusCodes.Status400BadRequest,
                "Некорректный запрос",
                "Проверьте параметры запроса."),
            ["service_unavailable"] = new(
                StatusCodes.Status503ServiceUnavailable,
                "Сервис временно недоступен",
                "Повторите попытку позже.")
        };

    public SarafanProblemDetails Create(
        HttpContext context,
        int statusCode,
        string code,
        IReadOnlyDictionary<string, string[]>? errors = null,
        PhoneValidationReason? phoneValidationReason = null)
        => OperationLogging.Run(_logger, $"{typeof(SarafanProblemDetailsFactory).FullName}.{nameof(Create)}",
            () => ProblemInputs(statusCode, code, phoneValidationReason), () =>
            {
                var details = CreateCore(context, statusCode, code, errors, phoneValidationReason);
                LogProblemEmitted(details);
                return details;
            });

    private SarafanProblemDetails CreateCore(
        HttpContext context, int statusCode, string code, IReadOnlyDictionary<string, string[]>? errors,
        PhoneValidationReason? phoneValidationReason)
    {
        if (!Definitions.TryGetValue(code, out var definition)
            || definition.StatusCode != statusCode)
        {
            code = "internal_error";
            definition = Definitions[code];
        }

        var traceId = SarafanTraceIdentifiers.GetOrCreate(context);
        var field = code switch
        {
            "invalid_service_catalogue_service" => "service",
            "service_catalogue_product_reserved" => "service",
            "invalid_service_catalogue_method" => "priceMethod",
            "invalid_service_catalogue_bands" => "bands",
            "invalid_service_catalogue_currency" => "currency",
            "invalid_service_catalogue_percentage" => "percentage",
            "invalid_service_catalogue_amount" => "amount",
            "invalid_service_catalogue_minimum_amount" => "minimumAmount",
            "invalid_service_catalogue_maximum_amount" => "maximumAmount",
            "invalid_service_catalogue_version" or "service_catalogue_update_conflict" => "version",
            "store_display_order_conflict" => "displayOrder",
            "invalid_store_url" => "officialUrl",
            "store_priority_limit_exceeded" => "status",
            "store_logo_required" => "logo",
            "order_quantity_limit_exceeded" => "quantity",
            "order_value_limit_exceeded" => "sellerPrice",
            "invalid_order_product_name" => "productName",
            "invalid_order_store_name" => "storeName",
            "invalid_order_seller_price" => "sellerPrice",
            "invalid_order_color" => "color",
            "invalid_order_size" => "size",
            "invalid_order_quantity" => "quantity",
            "invalid_order_comment" => "comment",
            _ => null
        };
        if (errors is null && field is not null)
            errors = new Dictionary<string, string[]> { [field] = [definition.Detail] };
        context.Response.Headers.ContentLanguage = "ru";
        var details = new SarafanProblemDetails
        {
            Type = $"{TypeBase}{code.Replace('_', '-')}",
            Title = definition.Title,
            Status = definition.StatusCode,
            Detail = code == "invalid_phone" ? PhoneDetail(phoneValidationReason, definition.Detail) : definition.Detail,
            Instance = $"urn:sarafan:problem:{traceId}",
            Code = code,
            Errors = errors,
            TraceId = traceId
        };
        return details;
    }

    public ObjectResult CreateResult(HttpContext context, int statusCode, string code)
        => OperationLogging.Run(_logger, $"{typeof(SarafanProblemDetailsFactory).FullName}.{nameof(CreateResult)}",
            () => ProblemInputs(statusCode, code), () => Result(Create(context, statusCode, code)));

    public ObjectResult CreateValidationResult(HttpContext context, ModelStateDictionary modelState)
        => OperationLogging.Run(_logger, $"{typeof(SarafanProblemDetailsFactory).FullName}.{nameof(CreateValidationResult)}",
            () => "context/modelState=[redacted]", () => CreateValidationResultCore(context, modelState));

    private ObjectResult CreateValidationResultCore(HttpContext context, ModelStateDictionary modelState)
    {
        var errors = modelState
            .Where(item => item.Value?.ValidationState == ModelValidationState.Invalid)
            .GroupBy(item => IsQuantityBindingPath(item.Key)
                ? "quantity" : JsonNamingPolicy.CamelCase.ConvertName(item.Key), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(item => IsQuantityBindingPath(item.Key)
                    ? new[] { "Количество должно быть целым числом." }
                    : ValidationMessages(item.Value)).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        return Result(Create(
            context,
            StatusCodes.Status400BadRequest,
            "validation_failed",
            errors));
    }

    public ValueTask WriteAsync(
        HttpContext context,
        int statusCode,
        string code,
        CancellationToken cancellationToken = default,
        Guid? requiredDocumentId = null,
        LegalDocumentKind? consentKind = null,
        AuthenticationFlowStep? nextStep = null,
        IReadOnlyList<LegalDocumentKind>? requiredDocumentKinds = null,
        PhoneValidationReason? phoneValidationReason = null,
        IReadOnlyDictionary<string, string[]>? errors = null)
        => new(OperationLogging.RunAsync(_logger, $"{typeof(SarafanProblemDetailsFactory).FullName}.{nameof(WriteAsync)}",
            () => ProblemInputs(statusCode, code, phoneValidationReason), () => WriteCoreAsync(context, statusCode, code, cancellationToken,
                requiredDocumentId, consentKind, nextStep, requiredDocumentKinds, phoneValidationReason, errors), cancellationToken));

    private async Task WriteCoreAsync(
        HttpContext context,
        int statusCode,
        string code,
        CancellationToken cancellationToken,
        Guid? requiredDocumentId,
        LegalDocumentKind? consentKind,
        AuthenticationFlowStep? nextStep,
        IReadOnlyList<LegalDocumentKind>? requiredDocumentKinds,
        PhoneValidationReason? phoneValidationReason,
        IReadOnlyDictionary<string, string[]>? errors)
    {
        var details = CreateCore(context, statusCode, code, errors, phoneValidationReason);
        if (requiredDocumentId is not null && consentKind is { } kind && Enum.IsDefined(kind))
        {
            details.Extensions["requiredDocumentId"] = requiredDocumentId;
            details.Extensions["consentKind"] = (int)kind;
        }
        if (nextStep is { } step && Enum.IsDefined(step))
        {
            details.Extensions["nextStep"] = (int)step;
            details.Extensions["requiredDocumentKinds"] = requiredDocumentKinds?.Select(kind => (int)kind).ToArray() ?? [];
        }
        context.Response.StatusCode = details.Status!.Value;
        context.Response.ContentType = MediaType;
        context.Response.Headers.ContentLanguage = "ru";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            details,
            SerializerOptions,
            cancellationToken);
        LogProblemEmitted(details);
    }

    private static string PhoneDetail(PhoneValidationReason? reason, string fallback) => reason switch
    {
        PhoneValidationReason.Empty => "Введите номер телефона.",
        PhoneValidationReason.UnsupportedCharacters => "Используйте цифры, обычные пробелы, скобки и дефисы. Знак + допускается только в начале номера.",
        PhoneValidationReason.WrongPrefix => "Начните номер с +7 или 8. Например: +7 (921) 123-45-67.",
        PhoneValidationReason.TooShort => "Номер слишком короткий. Введите 11 цифр, начиная с 8 или +7.",
        PhoneValidationReason.TooLong => "Номер слишком длинный. Введите 11 цифр, начиная с 8 или +7.",
        PhoneValidationReason.FormattedDomesticNumber => "Для номера с пробелами, скобками или дефисами замените начальную 8 на +7. Например: +7 (921) 123-45-67.",
        _ => fallback
    };

    private void LogProblemEmitted(SarafanProblemDetails details)
        => SarafanEvents.ProblemEmitted(
            _logger,
            details.Status!.Value,
            details.Type!,
            details.Code,
            details.Instance!,
            details.TraceId!);

    public static string CodeForStatus(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "bad_request",
        StatusCodes.Status401Unauthorized => "invalid_access_token",
        StatusCodes.Status403Forbidden => "access_denied",
        StatusCodes.Status404NotFound => "resource_not_found",
        StatusCodes.Status405MethodNotAllowed => "method_not_allowed",
        StatusCodes.Status413PayloadTooLarge => "request_too_large",
        StatusCodes.Status415UnsupportedMediaType => "unsupported_media_type",
        StatusCodes.Status429TooManyRequests => "rate_limited",
        StatusCodes.Status503ServiceUnavailable => "service_unavailable",
        >= 500 and < 600 => "internal_error",
        _ => "internal_error"
    };

    private static string ProblemInputs(int statusCode, string code, PhoneValidationReason? phoneValidationReason = null)
        => $"statusCode={statusCode}; code={(code is not null && Definitions.ContainsKey(code) ? code : "other")}; phoneValidationReason={PhoneReasonSummary(phoneValidationReason)}; context/errors=[redacted]";

    private static string PhoneReasonSummary(PhoneValidationReason? reason) => reason switch
    {
        null => "null",
        PhoneValidationReason.Empty => nameof(PhoneValidationReason.Empty),
        PhoneValidationReason.UnsupportedCharacters => nameof(PhoneValidationReason.UnsupportedCharacters),
        PhoneValidationReason.WrongPrefix => nameof(PhoneValidationReason.WrongPrefix),
        PhoneValidationReason.TooShort => nameof(PhoneValidationReason.TooShort),
        PhoneValidationReason.TooLong => nameof(PhoneValidationReason.TooLong),
        PhoneValidationReason.FormattedDomesticNumber => nameof(PhoneValidationReason.FormattedDomesticNumber),
        _ => "other"
    };

    private static ObjectResult Result(SarafanProblemDetails details)
    {
        var result = new ObjectResult(details)
        {
            StatusCode = details.Status
        };
        result.ContentTypes.Add(MediaType);
        return result;
    }

    // JSON conversion fails before property validation; never expose the formatter exception or input.
    private static bool IsQuantityBindingPath(string key)
        => string.Equals(key, "$.quantity", StringComparison.OrdinalIgnoreCase);

    private static string[] ValidationMessages(ModelStateEntry? entry)
    {
        if (entry is null || entry.Errors.Count == 0)
        {
            return ["Значение заполнено некорректно."];
        }

        return entry.Errors
            .Select(error => error.Exception is null && ContainsCyrillic(error.ErrorMessage)
                ? error.ErrorMessage.Trim()
                : "Значение заполнено некорректно.")
            .ToArray();
    }

    private static bool ContainsCyrillic(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Any(character => character is >= '\u0400' and <= '\u04ff');

    private sealed record ProblemDefinition(int StatusCode, string Title, string Detail);
}
