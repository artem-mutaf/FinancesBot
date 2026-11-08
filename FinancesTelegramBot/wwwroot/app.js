'use strict';

const tg = window.Telegram.WebApp;
tg.ready();   // сообщаем Telegram, что страница загрузилась
tg.expand();  // раскрываем Mini App на весь экран

// =====================================================================
//  Состояние приложения — единственный источник правды для отрисовки.
//  Схема простая: загрузили данные -> положили в state -> перерисовали экран.
// =====================================================================
const state = {
    me: null,
    accounts: [],
    categories: [],
    operations: [],
};

let money = createFormatter('RUB');

const screens = {
    home: document.getElementById('screen-home'),
    operation: document.getElementById('screen-operation'),
    account: document.getElementById('screen-account'),
};
const greeting = document.getElementById('greeting');
const currencySelect = document.getElementById('currency');

const ACCOUNT_ICONS = { Card: '💳', Cash: '💵', Savings: '🐷' };
const ACCOUNT_TYPE_NAMES = { Card: 'Карта', Cash: 'Наличные', Savings: 'Накопления' };
const CATEGORY_ICONS = {
    'Продукты': '🛒', 'Транспорт': '🚌', 'Кафе': '☕', 'Дом': '🏠', 'Здоровье': '💊',
    'Развлечения': '🎬', 'Зарплата': '💼', 'Подарки': '🎁', 'Другое': '📦',
};

// =====================================================================
//  Утилиты
// =====================================================================

/**
 * Мини-конструктор DOM: el('div', { className: 'row', onclick: fn }, 'текст', childEl).
 * Строки добавляются как ТЕКСТ (не HTML), поэтому пользовательские данные
 * вроде названия счёта "<script>" не выполнятся — защита от XSS.
 */
function el(tag, props = {}, ...children) {
    const node = document.createElement(tag);
    for (const [key, value] of Object.entries(props)) {
        if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
        else if (key in node) node[key] = value;
        else node.setAttribute(key, value);
    }
    node.append(...children.flat().filter(c => c !== null && c !== undefined && c !== false));
    return node;
}

function createFormatter(currency) {
    // Intl.NumberFormat сам знает символ и формат каждой валюты по её ISO-коду.
    return new Intl.NumberFormat('ru-RU', { style: 'currency', currency });
}

/** Дата в формате YYYY-MM-DD по МЕСТНОМУ времени (toISOString дал бы UTC и мог сдвинуть день). */
function localIsoDate(offsetDays = 0) {
    const d = new Date();
    d.setDate(d.getDate() + offsetDays);
    const pad = n => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

function formatDateHeading(iso) {
    if (iso === localIsoDate(0)) return 'Сегодня';
    if (iso === localIsoDate(-1)) return 'Вчера';
    const [y, m, d] = iso.split('-').map(Number);
    const date = new Date(y, m - 1, d);
    const sameYear = y === new Date().getFullYear();
    return new Intl.DateTimeFormat('ru-RU', {
        day: 'numeric', month: 'long', ...(sameYear ? {} : { year: 'numeric' }),
    }).format(date);
}

/**
 * "1 234,5" -> 1234.5. Возвращает NaN, если формат неверный.
 * Используем input type="text" + inputmode="decimal", а не type="number":
 * у type="number" проблемы с запятой в русской раскладке.
 */
function parseAmount(text, { allowNegative = false } = {}) {
    const s = String(text).replace(/\s/g, '');
    const pattern = allowNegative ? /^-?\d{1,13}([.,]\d{1,2})?$/ : /^\d{1,13}([.,]\d{1,2})?$/;
    return pattern.test(s) ? Number(s.replace(',', '.')) : NaN;
}

function categoryIcon(name) { return CATEGORY_ICONS[name] ?? '🏷️'; }

// Нативные диалоги Telegram, обёрнутые в Promise, чтобы использовать с await.
function confirmAsync(message) {
    return new Promise(resolve =>
        tg.isVersionAtLeast('6.2') ? tg.showConfirm(message, resolve) : resolve(window.confirm(message)));
}
function alertAsync(message) {
    return new Promise(resolve =>
        tg.isVersionAtLeast('6.2') ? tg.showAlert(message, resolve) : (window.alert(message), resolve()));
}

function haptic(type) {
    // HapticFeedback — вибрация телефона (есть не во всех клиентах, поэтому ?.)
    tg.HapticFeedback?.notificationOccurred(type);
}

// =====================================================================
//  API
// =====================================================================

/**
 * Обёртка над fetch: к каждому запросу добавляет заголовок с подписанным initData.
 * Сервер проверит подпись и поймёт, кто мы. Ошибки сервера (ProblemDetails)
 * превращаем в Error с понятным текстом.
 */
async function api(path, options = {}) {
    const response = await fetch(path, {
        ...options,
        headers: {
            'Authorization': `tma ${tg.initData}`,
            'Content-Type': 'application/json',
            ...options.headers,
        },
    });

    if (!response.ok) {
        let message = `Ошибка ${response.status}`;
        try {
            const problem = await response.json();
            if (problem.errors) message = Object.values(problem.errors).flat().join('\n');
            else message = problem.detail ?? problem.title ?? message;
        } catch { /* тело не JSON — оставляем код ошибки */ }
        throw new Error(message);
    }

    return response.status === 204 ? null : response.json(); // 204 No Content — тела нет
}

/** Загружаем счета и операции параллельно — Promise.all ждёт оба запроса. */
async function reloadData() {
    [state.accounts, state.operations] = await Promise.all([
        api('/api/accounts'),
        api('/api/operations?limit=50'),
    ]);
}

// =====================================================================
//  Нативные кнопки Telegram (MainButton внизу, BackButton в шапке)
//  У каждой может быть только один наш обработчик — старый снимаем.
// =====================================================================
let mainButtonHandler = null;
function setMainButton(text, handler) {
    if (mainButtonHandler) tg.MainButton.offClick(mainButtonHandler);
    mainButtonHandler = handler;
    tg.MainButton.hideProgress();
    if (!text) { tg.MainButton.hide(); return; }
    tg.MainButton.setText(text);
    tg.MainButton.onClick(handler);
    tg.MainButton.show();
}

let backButtonHandler = null;
function setBackButton(handler) {
    if (backButtonHandler) tg.BackButton.offClick(backButtonHandler);
    backButtonHandler = handler;
    if (!handler) { tg.BackButton.hide(); return; }
    tg.BackButton.onClick(handler);
    tg.BackButton.show();
}

function showScreen(name) {
    for (const [key, node] of Object.entries(screens)) node.hidden = key !== name;
    window.scrollTo(0, 0);
}

/**
 * Общая логика "Сохранить": блокируем повторные нажатия, крутим индикатор на кнопке,
 * показываем ошибку в форме, при успехе — вибрация и возврат на главный.
 */
function makeSubmitHandler(errorNode, action) {
    let busy = false;
    return async () => {
        if (busy) return;
        busy = true;
        errorNode.hidden = true;
        tg.MainButton.showProgress(false);
        try {
            await action();
            await reloadData();
            haptic('success');
            openHome();
        } catch (e) {
            errorNode.textContent = e.message;
            errorNode.hidden = false;
            haptic('error');
        } finally {
            tg.MainButton.hideProgress();
            busy = false;
        }
    };
}

// =====================================================================
//  Экран: главный
// =====================================================================
function openHome() {
    showScreen('home');
    setBackButton(null);
    setMainButton(state.accounts.length > 0 ? 'Добавить операцию' : null, () => openOperationForm());
    renderHome();
}

function renderHome() {
    const total = state.accounts.reduce((sum, a) => sum + a.balance, 0);

    screens.home.replaceChildren(
        el('div', { className: 'hint' }, 'Всего'),
        el('div', { className: 'total' + (total < 0 ? ' negative' : '') }, money.format(total)),

        el('div', { className: 'section-title' }, 'Счета'),
        state.accounts.length > 0
            ? el('div', { className: 'list' }, state.accounts.map(renderAccountRow))
            : el('div', { className: 'empty' }, 'Счетов пока нет'),
        el('button', { className: 'link-button', onclick: () => openAccountForm(null) }, '+ Новый счёт'),

        el('div', { className: 'section-title' }, 'Последние операции'),
        renderOperations(),
    );
}

function renderAccountRow(account) {
    return el('button', { className: 'row', onclick: () => openAccountForm(account) },
        el('span', { className: 'row-icon' }, ACCOUNT_ICONS[account.type] ?? '💰'),
        el('div', { className: 'row-main' },
            el('div', { className: 'row-title' }, account.name)),
        el('span', { className: 'amount' + (account.balance < 0 ? ' negative' : '') },
            money.format(account.balance)),
    );
}

function renderOperations() {
    if (state.operations.length === 0)
        return el('div', { className: 'empty' }, 'Операций пока нет.\nНажми «Добавить операцию» внизу.');

    // Операции уже отсортированы сервером по дате; вставляем заголовок при смене дня.
    const nodes = [];
    let currentDate = null;
    let currentList = null;
    for (const op of state.operations) {
        if (op.date !== currentDate) {
            currentDate = op.date;
            currentList = el('div', { className: 'list' });
            nodes.push(el('div', { className: 'date-heading' }, formatDateHeading(op.date)), currentList);
        }
        currentList.append(renderOperationRow(op));
    }
    return el('div', {}, nodes);
}

function renderOperationRow(op) {
    let icon, title, subtitle, amountText, amountClass;

    switch (op.type) {
        case 'Expense':
            icon = categoryIcon(op.categoryName);
            title = op.categoryName;
            subtitle = op.accountName;
            amountText = '−' + money.format(op.amount);
            amountClass = '';
            break;
        case 'Income':
            icon = categoryIcon(op.categoryName);
            title = op.categoryName;
            subtitle = op.accountName;
            amountText = '+' + money.format(op.amount);
            amountClass = 'income';
            break;
        case 'Transfer':
            icon = '🔄';
            title = 'Перевод';
            subtitle = `${op.accountName} → ${op.toAccountName}`;
            amountText = money.format(op.amount);
            amountClass = 'transfer';
            break;
    }
    if (op.description) subtitle = `${op.description} · ${subtitle}`;

    return el('button', { className: 'row', onclick: () => deleteOperation(op) },
        el('span', { className: 'row-icon' }, icon),
        el('div', { className: 'row-main' },
            el('div', { className: 'row-title' }, title),
            el('div', { className: 'row-subtitle' }, subtitle)),
        el('span', { className: `amount ${amountClass}` }, amountText),
    );
}

async function deleteOperation(op) {
    const ok = await confirmAsync(`Удалить операцию на ${money.format(op.amount)}?`);
    if (!ok) return;
    try {
        await api(`/api/operations/${op.id}`, { method: 'DELETE' });
        await reloadData();
        haptic('success');
        renderHome();
    } catch (e) {
        await alertAsync(e.message);
    }
}

// =====================================================================
//  Экран: новая операция
// =====================================================================
function openOperationForm() {
    showScreen('operation');
    setBackButton(openHome);

    // Локальное состояние формы. Замыкание: функции ниже "видят" эту переменную.
    const form = {
        type: 'Expense',
        categoryId: null,
    };

    const accountOption = a =>
        el('option', { value: a.id }, `${ACCOUNT_ICONS[a.type] ?? ''} ${a.name} · ${money.format(a.balance)}`);

    // ---- Поля ----
    const typeButtons = [['Expense', 'Расход'], ['Income', 'Доход'], ['Transfer', 'Перевод']]
        .map(([type, label]) => el('button', {
            type: 'button',
            onclick: () => { form.type = type; form.categoryId = null; update(); },
        }, label));

    const amountInput = el('input', {
        className: 'amount-input', inputMode: 'decimal', placeholder: '0', autocomplete: 'off',
    });

    const accountLabel = el('span', { className: 'field-label' });
    const accountSelect = el('select', {}, state.accounts.map(accountOption));

    const toAccountSelect = el('select', {}, state.accounts.map(accountOption));
    // По умолчанию "куда" — первый счёт накоплений, иначе второй по списку.
    const defaultTarget = state.accounts.find(a => a.type === 'Savings') ?? state.accounts[1];
    if (defaultTarget) toAccountSelect.value = defaultTarget.id;
    const toAccountField = el('label', { className: 'field' },
        el('span', { className: 'field-label' }, 'На счёт'), toAccountSelect);

    const chips = el('div', { className: 'chips' });
    const categoryField = el('div', { className: 'field' },
        el('span', { className: 'field-label' }, 'Категория'), chips);

    const descriptionInput = el('input', { placeholder: 'Например: Пятёрочка', maxLength: 500 });
    const dateInput = el('input', { type: 'date', value: localIsoDate(0), max: localIsoDate(0) });
    const errorNode = el('p', { className: 'form-error', hidden: true });

    screens.operation.replaceChildren(
        el('div', { className: 'form-title' }, 'Новая операция'),
        el('div', { className: 'segmented' }, typeButtons),
        errorNode,
        el('label', { className: 'field' }, el('span', { className: 'field-label' }, 'Сумма'), amountInput),
        el('label', { className: 'field' }, accountLabel, accountSelect),
        toAccountField,
        categoryField,
        el('label', { className: 'field' }, el('span', { className: 'field-label' }, 'Описание'), descriptionInput),
        el('label', { className: 'field' }, el('span', { className: 'field-label' }, 'Дата'), dateInput),
    );

    /** Перерисовывает то, что зависит от типа операции. */
    function update() {
        typeButtons.forEach((btn, i) =>
            btn.classList.toggle('active', ['Expense', 'Income', 'Transfer'][i] === form.type));

        const isTransfer = form.type === 'Transfer';
        accountLabel.textContent = { Expense: 'Со счёта', Income: 'На счёт', Transfer: 'Со счёта' }[form.type];
        toAccountField.hidden = !isTransfer;
        categoryField.hidden = isTransfer;

        if (!isTransfer) {
            const categories = state.categories.filter(c => c.type === form.type);
            chips.replaceChildren(...categories.map(c => el('button', {
                type: 'button',
                className: 'chip' + (c.id === form.categoryId ? ' active' : ''),
                onclick: () => { form.categoryId = c.id; tg.HapticFeedback?.selectionChanged(); update(); },
            }, `${categoryIcon(c.name)} ${c.name}`)));
        }
    }

    // ---- Сохранение ----
    setMainButton('Сохранить', makeSubmitHandler(errorNode, async () => {
        // Быстрые проверки на клиенте — для удобства (сразу видно ошибку).
        // Настоящая защита — валидация на сервере: клиенту доверять нельзя.
        const amount = parseAmount(amountInput.value);
        if (!(amount > 0)) throw new Error('Введите сумму, например 1500 или 99,90');
        if (form.type !== 'Transfer' && form.categoryId === null) throw new Error('Выберите категорию');

        await api('/api/operations', {
            method: 'POST',
            body: JSON.stringify({
                type: form.type,
                amount,
                accountId: Number(accountSelect.value),
                toAccountId: form.type === 'Transfer' ? Number(toAccountSelect.value) : null,
                categoryId: form.type === 'Transfer' ? null : form.categoryId,
                description: descriptionInput.value,
                date: dateInput.value,
            }),
        });
    }));

    update();
    amountInput.focus();
}

// =====================================================================
//  Экран: счёт (создание и редактирование)
// =====================================================================
function openAccountForm(account) {
    const isNew = account === null;
    showScreen('account');
    setBackButton(openHome);

    const nameInput = el('input', {
        value: account?.name ?? '', placeholder: 'Например: Тинькофф', maxLength: 100,
    });
    const typeSelect = el('select', {},
        Object.entries(ACCOUNT_TYPE_NAMES).map(([value, label]) =>
            el('option', { value }, `${ACCOUNT_ICONS[value]} ${label}`)));
    const balanceInput = el('input', {
        className: 'amount-input', inputMode: 'decimal', placeholder: '0', autocomplete: 'off',
        value: account ? String(account.balance).replace('.', ',') : '',
    });
    const errorNode = el('p', { className: 'form-error', hidden: true });

    screens.account.replaceChildren(
        el('div', { className: 'form-title' }, isNew ? 'Новый счёт' : 'Счёт'),
        errorNode,
        el('label', { className: 'field' }, el('span', { className: 'field-label' }, 'Название'), nameInput),
        // Тип выбираем только при создании (у существующего счёта он не меняется)
        isNew && el('label', { className: 'field' }, el('span', { className: 'field-label' }, 'Тип'), typeSelect),
        el('label', { className: 'field' },
            el('span', { className: 'field-label' }, 'Сколько денег на счёте сейчас'), balanceInput),
        !isNew && el('button', { className: 'danger-button', onclick: () => deleteAccount(account) }, 'Удалить счёт'),
    );

    setMainButton('Сохранить', makeSubmitHandler(errorNode, async () => {
        const balance = parseAmount(balanceInput.value || '0', { allowNegative: true });
        if (Number.isNaN(balance)) throw new Error('Введите сумму, например 15000 или 99,90');

        if (isNew) {
            await api('/api/accounts', {
                method: 'POST',
                body: JSON.stringify({ name: nameInput.value, type: typeSelect.value, balance }),
            });
        } else {
            await api(`/api/accounts/${account.id}`, {
                method: 'PUT',
                body: JSON.stringify({ name: nameInput.value, balance }),
            });
        }
    }));

    (isNew ? nameInput : balanceInput).focus();
}

async function deleteAccount(account) {
    const ok = await confirmAsync(`Удалить счёт «${account.name}»?`);
    if (!ok) return;
    try {
        await api(`/api/accounts/${account.id}`, { method: 'DELETE' });
        await reloadData();
        haptic('success');
        openHome();
    } catch (e) {
        await alertAsync(e.message); // например: "По счёту есть операции. Сначала удали их."
    }
}

// =====================================================================
//  Валюта
// =====================================================================
function applyCurrency(currency) {
    currencySelect.value = currency;
    money = createFormatter(currency);
}

currencySelect.addEventListener('change', async () => {
    currencySelect.disabled = true; // защита от повторных нажатий, пока идёт запрос
    try {
        state.me = await api('/api/me/currency', {
            method: 'PUT',
            body: JSON.stringify({ currency: currencySelect.value }),
        });
        applyCurrency(state.me.currency);
        tg.HapticFeedback?.selectionChanged();
        openHome(); // перерисовываем суммы (и закрываем форму, если была открыта)
    } catch (e) {
        await alertAsync(e.message);
    } finally {
        currencySelect.disabled = false;
    }
});

// =====================================================================
//  Старт
// =====================================================================
async function init() {
    if (!tg.initData) {
        greeting.textContent = 'Открой приложение через бота в Telegram.';
        return;
    }
    try {
        state.me = await api('/api/me'); // создаст пользователя при первом входе
        greeting.textContent = `Привет, ${state.me.firstName}!`;
        applyCurrency(state.me.currency);
        currencySelect.hidden = false;

        // Категории меняются редко — грузим один раз; счета и операции — через reloadData.
        [state.categories] = await Promise.all([api('/api/categories'), reloadData()]);
        openHome();
    } catch (e) {
        screens.home.replaceChildren(el('p', { className: 'error' }, `Ошибка: ${e.message}`));
    }
}

init();
