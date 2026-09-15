# Проверка товара — API 0.1.1

Основание: [Core #34](https://github.com/sara-fan/sarafan.core/issues/34), [нормативная поправка v1.22](https://github.com/sara-fan/sarafan.spec/blob/main/spec/Product%20review%20amendment.md), разделы 1–5 (поправки к §§4.2–4.5, 4.14–4.15 и 4.17 базовой спецификации).

## Создание и повтор

`POST /api/v1/orders`, customer Bearer, актуальное согласие и UUID `Idempotency-Key`:

```json
{
  "sourceUrl": "https://shop.example.com/product",
  "quantity": 2,
  "comment": "Проверить комплектацию",
  "submittedProduct": {
    "productName": "STANLEY Quencher H2.0",
    "sellerPrice": { "amount": 40.00, "currency": 840 },
    "color": "cherry blossom",
    "size": null
  }
}
```

Новая заявка требует название, цену USD и количество 1–4. Эквивалентный повтор сравнивается с исходными полями, независимо от исправлений сотрудника и текущих курсов; исторический запрос без новых полей остаётся воспроизводимым. Не следует сравнивать отправленную заявку с актуальным `product` ответа: сравнивается `submittedProduct` вместе с исходной ссылкой.

Клиентские детали сохраняют прежние верхнеуровневые поля, но название/цена/количество/комментарий теперь актуальные. Дополнительно возвращаются `product`, `submittedProduct`, `createdAt`, `showReviewFields`. Внутри обоих product находятся `productName`, `sellerPrice`, `quantity`, `color`, `size`, `comment`.

## Карточка сотрудника

`GET /api/v1/backoffice/orders/{orderNumber}` возвращает публичный номер, статус, исходную ссылку, createdAt/updatedAt, product/submittedProduct, текущий customer, limitCheck и canEditProduct. Покупательский токен не предоставляет доступ. Nullable-поля профиля остаются null.

`PUT /api/v1/backoffice/orders/{orderNumber}/product`:

```json
{
  "expectedUpdatedAt": "2026-09-15T12:00:00.123456+00:00",
  "productName": "STANLEY Quencher H2.0",
  "sellerPrice": { "amount": 40.00, "currency": 840 },
  "quantity": 2,
  "color": "cherry blossom",
  "size": null,
  "comment": "Проверено"
}
```

Передавать `expectedUpdatedAt` точно как строку ответа GET, без преобразования через JavaScript Date и потери микросекунд. Ответ — полная новая карточка. `409 order_update_conflict` требует перезагрузки, а не повтора с новым timestamp. При выходе из UnderReview возвращается `409 order_not_editable`. Исходная заявка, URL, покупатель и статус не изменяются; исправления и аудит записываются одной транзакцией.

## Метаданные и курсы

Оба `/orders/ops` и `/backoffice/orders/ops` возвращают `productLimits`: minimumQuantity/maximumQuantity/defaultQuantity, productNameMaximumLength/colorMaximumLength/sizeMaximumLength/commentMaximumLength, sellerPriceCurrency, maximumUnitPrice, priceDecimalPlaces и `valueLimit`. Поля valueLimit: maximumAmount=1000, currency=978, available, sourceEffectiveDate, maximumTotalUsd. Последнее округляется вниз до цента и ограничено максимально представимой ценой × 4; это максимум общей суммы, не цены за единицу. Сервер всегда повторяет точную проверку на запись.

`/backoffice/status` возвращает последние доступные USD/RUB и EUR/RUB независимо. Для лимита используется последняя общая дата, поэтому дата в limitCheck может отличаться от более свежего отдельного курса в status. Отсутствие пары не ломает чтение и Ops; новые записи получают `503 order_limit_rates_unavailable`.

## Выпуск

1. Согласовать нормативную поправку и внедрить UI #28 / back.office #15 на этом контракте.
2. При плановом выпуске применить nullable-миграцию `0_1_1_OrderProducts` к целевой базе через существующий процесс развёртывания. Она не заполняет вымышленные данные исторических заказов.
3. Дождаться общей пары курсов и каталога IANA перед открытием создания новых заявок. Не заменять коммерческий AppliedExchangeRate курсом проверки лимита.

Core и клиентская форма выпускаются согласованно: старый клиент без submittedProduct получает ошибку на новую заявку. Откат миграции после появления исправлений удаляет новые поля/аудит, а наличие EUR несовместимо со старыми ограничениями валют; для работающей системы предпочтительно исправление вперёд.

В этой поставке нет промышленного парсера, прогнозной/финальной стоимости, подтверждения расчёта и отправки SMS. Проверки используют фикстуры ЦБ, EF InMemory и отключённую модель Npgsql; миграции, блокировки и ограничения PostgreSQL в тестах не исполняются. Локальные защищённые данные и Docker-конфигурация не изменяются.
