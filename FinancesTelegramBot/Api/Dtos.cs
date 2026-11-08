using FinancesTelegramBot.Data.Entities;

namespace FinancesTelegramBot.Api;

// DTO (Data Transfer Object) — то, что API принимает и отдаёт в JSON.
//
// Почему не отдавать сущности (Account, AppUser) напрямую?
//  - наружу ушли бы лишние поля и навигации (а с циклическими ссылками сериализатор вообще упадёт);
//  - любое изменение БД ломало бы фронтенд;
//  - в DTO можно добавить вычисляемое (Balance), которого в сущности нет;
//  - во входящих DTO (*Request) клиент физически не сможет прислать то, что менять нельзя (UserId, Id).
//
// record — неизменяемый класс с готовыми Equals/ToString; идеален для DTO.

// ---------- Профиль ----------
public sealed record MeDto(long Id, string FirstName, string? Username, Currency Currency);

/// <summary>Тело запроса PUT /api/me/currency: { "currency": "USD" }</summary>
public sealed record UpdateCurrencyRequest(Currency Currency);

// ---------- Счета ----------
public sealed record AccountDto(int Id, string Name, AccountType Type, decimal InitialBalance, decimal Balance);

/// <summary>Balance — сколько денег на счёте СЕЙЧАС (так пользователю понятнее, чем "начальный остаток").</summary>
public sealed record CreateAccountRequest(string Name, AccountType Type, decimal Balance);

public sealed record UpdateAccountRequest(string Name, decimal Balance);

// ---------- Категории ----------
public sealed record CategoryDto(int Id, string Name, CategoryType Type);

// ---------- Операции ----------
public sealed record OperationDto(
    int Id,
    OperationType Type,
    decimal Amount,
    int AccountId,
    string AccountName,
    int? ToAccountId,
    string? ToAccountName,
    int? CategoryId,
    string? CategoryName,
    string? Description,
    DateOnly Date);

public sealed record CreateOperationRequest(
    OperationType Type,
    decimal Amount,
    int AccountId,
    int? ToAccountId,
    int? CategoryId,
    string? Description,
    DateOnly Date);
