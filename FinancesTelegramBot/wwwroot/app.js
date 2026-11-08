'use strict';

const tg = window.Telegram.WebApp;
tg.ready();   // сообщаем Telegram, что страница загрузилась
tg.expand();  // раскрываем Mini App на весь экран

// Светлая/тёмная тема Telegram -> атрибут data-theme на <html>.
// CSS по нему переключает цвета графиков (основные цвета Telegram подставляет сам).
function applyTheme() { document.documentElement.dataset.theme = tg.colorScheme; }
applyTheme();
tg.onEvent('themeChanged', applyTheme);

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
    categories: document.getElementById('screen-categories'),
    category: document.getElementById('screen-category'),
    calendar: document.getElementById('screen-calendar'),
    stats: document.getElementById('screen-stats'),
};
const greeting = document.getElementById('greeting');
const currencySelect = document.getElementById('currency');

const ACCOUNT_ICONS = { Card: '💳', Cash: '💵', Savings: '🐷' };
const ACCOUNT_TYPE_NAMES = { Card: 'Карта', Cash: 'Наличные', Savings: 'Накопления' };
const OPERATION_TYPES = [['Expense', 'Расход'], ['Income', 'Доход'], ['Transfer', 'Перевод']];

/** Набор иконок для выбора в форме категории. */
const CATEGORY_ICON_CHOICES = [
    '🛒', '🍔', '☕', '🍕', '🍺', '🚌', '🚕', '⛽',
    '🚗', '🏠', '💡', '📱', '💻', '💊', '🏥', '💇',
    '👕', '🎬', '🎮', '🎵', '📚', '🎓', '✈️', '🏋️',
    '🐱', '🐶', '👶', '🎁', '🔧', '🧾', '📦', '🏷️',
    '💼', '💰', '📈', '🏦', '💸', '🤝', '🪙', '⭐',
];

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

/** Число -> строка для поля ввода: 1500.5 -> "1500,5" */
function amountToInput(value) {
    return String(value).replace('.', ',');
}

function categoryIcon(icon) { return icon || '🏷️'; }

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

/** Ошибка API: помимо текста хранит HTTP-статус и тело ProblemDetails. */
class ApiError extends Error {
    constructor(message, status, problem) {
        super(message);
        this.status = status;
        this.problem = problem;
    }
}

/**
 * Обёртка над fetch: к каждому запросу добавляет заголовок с подписанным initData.
 * Сервер проверит подпись и поймёт, кто мы. Ошибки сервера (ProblemDetails)
 * превращаем в ApiError с понятным текстом.
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
        let problem = null;
        try {
            problem = await response.json();
            if (problem.errors) message = Object.values(problem.errors).flat().join('\n');
            else message = problem.detail ?? problem.title ?? message;
        } catch { /* тело не JSON — оставляем код ошибки */ }
        throw new ApiError(message, response.status, problem);
    }

    return response.status === 204 ? null : response.json(); // 204 No Content — тела нет
}

/** Загружаем всё параллельно — Promise.all ждёт все запросы. */
async function reloadData() {
    [state.accounts, state.categories, state.operations] = await Promise.all([
        api('/api/accounts'),
        api('/api/categories'),
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
 * Общая логика "Сохранить"/"Удалить": блокируем повторные нажатия, крутим индикатор
 * на кнопке, показываем ошибку в форме, при успехе — перезагрузка данных, вибрация
 * и переход на экран goBack.
 */
function makeSubmitHandler(errorNode, action, goBack = openHome) {
    let busy = false;
    return async () => {
        if (busy) return;
        busy = true;
        errorNode.hidden = true;
        tg.MainButton.showProgress(false);
        try {
            const done = await action();
            if (done === false) return; // действие отменено (например, нажали "Нет" в подтверждении)
            await reloadData();
            haptic('success');
            goBack();
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

/** Поле формы: подпись + элемент ввода. */
function field(label, input, props = {}) {
    return el('label', { className: 'field', ...props }, el('span', { className: 'field-label' }, label), input);
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

        el('div', { className: 'nav-tiles' },
            navTile('📅', 'Календарь', () => openCalendar({ reset: true })),
            navTile('📊', 'Статистика', () => openStats({ reset: true })),
            navTile('🏷️', 'Категории', openCategories)),

        el('div', { className: 'section-title' }, 'Последние операции'),
        renderOperations(),
    );
}

function navTile(icon, label, onclick) {
    return el('button', { className: 'nav-tile', onclick }, el('span', {}, icon), el('span', {}, label));
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

/**
 * Строка операции. onOpen — что делать по нажатию: по умолчанию открыть редактирование
 * с возвратом на главный; календарь передаёт свой вариант (с возвратом в календарь).
 */
function renderOperationRow(op, onOpen = () => openOperationForm(op)) {
    let icon, title, subtitle, amountText, amountClass;

    switch (op.type) {
        case 'Expense':
            icon = categoryIcon(op.categoryIcon);
            title = op.categoryName;
            subtitle = op.accountName;
            amountText = '−' + money.format(op.amount);
            amountClass = '';
            break;
        case 'Income':
            icon = categoryIcon(op.categoryIcon);
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

    // Нажатие на операцию — открыть её для редактирования
    return el('button', { className: 'row', onclick: onOpen },
        el('span', { className: 'row-icon' }, icon),
        el('div', { className: 'row-main' },
            el('div', { className: 'row-title' }, title),
            el('div', { className: 'row-subtitle' }, subtitle)),
        el('span', { className: `amount ${amountClass}` }, amountText),
    );
}

// =====================================================================
//  Экран: операция (создание и редактирование)
//  operation === null  -> новая операция
//  operation = {...}   -> редактирование существующей
//  date    — дата по умолчанию для новой операции (из календаря)
//  goBack  — куда вернуться после сохранения / по кнопке "назад"
// =====================================================================
function openOperationForm(operation = null, { date = localIsoDate(0), goBack = openHome } = {}) {
    const isNew = operation === null;
    showScreen('operation');
    setBackButton(goBack);

    // Локальное состояние формы. Замыкание: функции ниже "видят" эту переменную.
    const form = {
        type: operation?.type ?? 'Expense',
        categoryId: operation?.categoryId ?? null,
    };

    const accountOption = a =>
        el('option', { value: a.id }, `${ACCOUNT_ICONS[a.type] ?? ''} ${a.name} · ${money.format(a.balance)}`);

    // ---- Поля ----
    const typeButtons = OPERATION_TYPES.map(([type, label]) => el('button', {
        type: 'button',
        onclick: () => {
            if (form.type === type) return;
            form.type = type;
            form.categoryId = null; // категории у расхода и дохода разные — сбрасываем выбор
            update();
        },
    }, label));

    const amountInput = el('input', {
        className: 'amount-input', inputMode: 'decimal', placeholder: '0', autocomplete: 'off',
        value: operation ? amountToInput(operation.amount) : '',
    });

    const accountLabel = el('span', { className: 'field-label' });
    const accountSelect = el('select', {}, state.accounts.map(accountOption));
    if (operation) accountSelect.value = operation.accountId;

    const toAccountSelect = el('select', {}, state.accounts.map(accountOption));
    // По умолчанию "куда" — первый счёт накоплений, иначе второй по списку.
    const defaultTarget = state.accounts.find(a => a.type === 'Savings') ?? state.accounts[1];
    toAccountSelect.value = operation?.toAccountId ?? defaultTarget?.id ?? '';
    const toAccountField = field('На счёт', toAccountSelect);

    const chips = el('div', { className: 'chips' });
    const categoryField = el('div', { className: 'field' },
        el('span', { className: 'field-label' }, 'Категория'), chips);

    const descriptionInput = el('input', {
        placeholder: 'Например: Пятёрочка', maxLength: 500, value: operation?.description ?? '',
    });
    const dateInput = el('input', {
        type: 'date', value: operation?.date ?? date, max: localIsoDate(0),
    });
    const errorNode = el('p', { className: 'form-error', hidden: true });

    const deleteButton = !isNew && el('button', {
        className: 'danger-button',
        onclick: makeSubmitHandler(errorNode, async () => {
            if (!await confirmAsync(`Удалить операцию на ${money.format(operation.amount)}?`)) return false;
            await api(`/api/operations/${operation.id}`, { method: 'DELETE' });
        }, goBack),
    }, 'Удалить операцию');

    screens.operation.replaceChildren(
        el('div', { className: 'form-title' }, isNew ? 'Новая операция' : 'Операция'),
        el('div', { className: 'segmented' }, typeButtons),
        errorNode,
        field('Сумма', amountInput),
        el('label', { className: 'field' }, accountLabel, accountSelect),
        toAccountField,
        categoryField,
        field('Описание', descriptionInput),
        field('Дата', dateInput),
        deleteButton,
    );

    /** Перерисовывает то, что зависит от типа операции. */
    function update() {
        typeButtons.forEach((btn, i) => btn.classList.toggle('active', OPERATION_TYPES[i][0] === form.type));

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
            }, `${categoryIcon(c.icon)} ${c.name}`)));
        }
    }

    // ---- Сохранение ----
    setMainButton('Сохранить', makeSubmitHandler(errorNode, async () => {
        // Быстрые проверки на клиенте — для удобства (сразу видно ошибку).
        // Настоящая защита — валидация на сервере: клиенту доверять нельзя.
        const amount = parseAmount(amountInput.value);
        if (!(amount > 0)) throw new Error('Введите сумму, например 1500 или 99,90');
        if (form.type !== 'Transfer' && form.categoryId === null) throw new Error('Выберите категорию');

        const body = JSON.stringify({
            type: form.type,
            amount,
            accountId: Number(accountSelect.value),
            toAccountId: form.type === 'Transfer' ? Number(toAccountSelect.value) : null,
            categoryId: form.type === 'Transfer' ? null : form.categoryId,
            description: descriptionInput.value,
            date: dateInput.value,
        });

        // Одна форма — два действия: POST создаёт, PUT изменяет существующую.
        if (isNew) await api('/api/operations', { method: 'POST', body });
        else await api(`/api/operations/${operation.id}`, { method: 'PUT', body });
    }, goBack));

    update();
    if (isNew) amountInput.focus();
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
        value: account ? amountToInput(account.balance) : '',
    });
    const errorNode = el('p', { className: 'form-error', hidden: true });

    const deleteButton = !isNew && el('button', {
        className: 'danger-button',
        onclick: makeSubmitHandler(errorNode, async () => {
            if (!await confirmAsync(`Удалить счёт «${account.name}»?`)) return false;
            await api(`/api/accounts/${account.id}`, { method: 'DELETE' });
        }),
    }, 'Удалить счёт');

    screens.account.replaceChildren(
        el('div', { className: 'form-title' }, isNew ? 'Новый счёт' : 'Счёт'),
        errorNode,
        field('Название', nameInput),
        // Тип выбираем только при создании (у существующего счёта он не меняется)
        isNew && field('Тип', typeSelect),
        field('Сколько денег на счёте сейчас', balanceInput),
        deleteButton,
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

// =====================================================================
//  Экран: список категорий
// =====================================================================
function openCategories() {
    showScreen('categories');
    setBackButton(openHome);
    setMainButton(null);

    const section = (type, title) => {
        const items = state.categories.filter(c => c.type === type);
        return [
            el('div', { className: 'section-title' }, title),
            el('div', { className: 'list' }, items.map(c =>
                el('button', { className: 'row', onclick: () => openCategoryForm(c, c.type) },
                    el('span', { className: 'row-icon' }, categoryIcon(c.icon)),
                    el('div', { className: 'row-main' }, el('div', { className: 'row-title' }, c.name))))),
            el('button', { className: 'link-button', onclick: () => openCategoryForm(null, type) },
                '+ Добавить категорию'),
        ];
    };

    screens.categories.replaceChildren(
        el('div', { className: 'form-title' }, 'Категории'),
        ...section('Expense', 'Расходы'),
        ...section('Income', 'Доходы'),
    );
}

// =====================================================================
//  Экран: категория (создание и редактирование)
// =====================================================================
function openCategoryForm(category, type) {
    const isNew = category === null;
    showScreen('category');
    setBackButton(openCategories);

    let selectedIcon = category?.icon ?? CATEGORY_ICON_CHOICES[0];

    const nameInput = el('input', {
        value: category?.name ?? '', placeholder: 'Например: Бензин', maxLength: 100,
    });

    // Сетка иконок: при выборе подсвечиваем выбранную
    const iconButtons = CATEGORY_ICON_CHOICES.map(icon => el('button', {
        type: 'button',
        className: 'icon-choice' + (icon === selectedIcon ? ' active' : ''),
        onclick: () => {
            selectedIcon = icon;
            iconButtons.forEach(b => b.classList.toggle('active', b.textContent === icon));
            tg.HapticFeedback?.selectionChanged();
        },
    }, icon));

    const errorNode = el('p', { className: 'form-error', hidden: true });

    // ---- Удаление (с возможным переносом операций) ----
    // Поле "Перенести операции в" появляется, только если сервер ответил 409 с operationsCount.
    const otherCategories = state.categories.filter(c => c.type === type && c.id !== category?.id);
    const moveSelect = el('select', {},
        otherCategories.map(c => el('option', { value: c.id }, `${categoryIcon(c.icon)} ${c.name}`)));
    const moveField = field('Перенести операции в', moveSelect, { hidden: true });

    const deleteButton = !isNew && el('button', {
        className: 'danger-button',
        onclick: makeSubmitHandler(errorNode, async () => {
            // Второй шаг: пользователь уже увидел поле переноса и выбрал категорию
            if (!moveField.hidden) {
                await api(`/api/categories/${category.id}?moveToCategoryId=${moveSelect.value}`, { method: 'DELETE' });
                return;
            }

            if (!await confirmAsync(`Удалить категорию «${category.name}»?`)) return false;
            try {
                await api(`/api/categories/${category.id}`, { method: 'DELETE' });
            } catch (e) {
                // 409 + operationsCount -> по категории есть операции: спрашиваем, куда перенести
                if (e instanceof ApiError && e.status === 409 && e.problem?.operationsCount) {
                    moveField.hidden = false;
                    deleteButton.textContent = `Перенести ${e.problem.operationsCount} опер. и удалить`;
                    moveSelect.focus();
                    return false; // остаёмся на экране
                }
                throw e;
            }
        }, openCategories),
    }, 'Удалить категорию');

    screens.category.replaceChildren(
        el('div', { className: 'form-title' },
            isNew ? (type === 'Expense' ? 'Новая категория расходов' : 'Новая категория доходов') : 'Категория'),
        errorNode,
        field('Название', nameInput),
        el('div', { className: 'field' }, el('span', { className: 'field-label' }, 'Иконка'),
            el('div', { className: 'icon-grid' }, iconButtons)),
        moveField,
        deleteButton,
    );

    setMainButton('Сохранить', makeSubmitHandler(errorNode, async () => {
        if (isNew) {
            await api('/api/categories', {
                method: 'POST',
                body: JSON.stringify({ name: nameInput.value, type, icon: selectedIcon }),
            });
        } else {
            await api(`/api/categories/${category.id}`, {
                method: 'PUT',
                body: JSON.stringify({ name: nameInput.value, icon: selectedIcon }),
            });
        }
    }, openCategories));

    if (isNew) nameInput.focus();
}

// =====================================================================
//  Экран: календарь
// =====================================================================
const WEEKDAYS = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'];

/** Состояние календаря живёт между открытиями: вернулись из формы — тот же месяц и день. */
const calendar = {
    year: 0,
    month: 0,              // 0..11, как в JS Date
    selected: null,        // выбранный день 'YYYY-MM-DD'
    days: new Map(),       // 'YYYY-MM-DD' -> итоги дня с сервера
    dayOperations: [],     // операции выбранного дня
    dayLoaded: false,
    // "Токены" запросов — защита от гонки: пользователь быстро листает месяцы, ответы приходят
    // в произвольном порядке. Применяем ответ, только если он от ПОСЛЕДНЕГО запроса.
    summaryToken: 0,
    dayToken: 0,
};

function isoDate(year, month, day) {
    const pad = n => String(n).padStart(2, '0');
    return `${year}-${pad(month + 1)}-${pad(day)}`;
}

function daysInMonth(year, month) {
    return new Date(year, month + 1, 0).getDate(); // 0-й день следующего месяца = последний день этого
}

/** reset: true — открыть на текущем месяце и сегодняшнем дне (вход с главного экрана). */
function openCalendar({ reset = false } = {}) {
    if (reset || calendar.selected === null) {
        const now = new Date();
        calendar.year = now.getFullYear();
        calendar.month = now.getMonth();
        calendar.selected = localIsoDate(0);
    }
    showScreen('calendar');
    setBackButton(openHome);
    renderCalendar();
    loadMonthSummary();
    loadSelectedDay();
}

function shiftMonth(delta) {
    // new Date сам "перенесёт" месяц через год: (2026, 12, 1) -> 1 января 2027
    const d = new Date(calendar.year, calendar.month + delta, 1);
    calendar.year = d.getFullYear();
    calendar.month = d.getMonth();

    // Листнули на текущий месяц — выбираем сегодня, иначе 1-е число
    const today = localIsoDate(0);
    const monthPrefix = isoDate(calendar.year, calendar.month, 1).slice(0, 7); // 'YYYY-MM'
    calendar.selected = today.startsWith(monthPrefix) ? today : `${monthPrefix}-01`;

    calendar.days = new Map();
    tg.HapticFeedback?.selectionChanged();
    renderCalendar();
    loadMonthSummary();
    loadSelectedDay();
}

function selectDay(iso) {
    if (iso === calendar.selected) return;
    calendar.selected = iso;
    tg.HapticFeedback?.selectionChanged();
    loadSelectedDay(); // сам перерисует экран
}

async function loadMonthSummary() {
    const token = ++calendar.summaryToken;
    const { year, month } = calendar;
    const from = isoDate(year, month, 1);
    const to = isoDate(year, month, daysInMonth(year, month));
    try {
        const days = await api(`/api/operations/daily?from=${from}&to=${to}`);
        if (token !== calendar.summaryToken) return; // пока ждали, пользователь листнул дальше
        calendar.days = new Map(days.map(d => [d.date, d]));
        renderCalendar();
    } catch (e) {
        if (token === calendar.summaryToken) await alertAsync(e.message);
    }
}

async function loadSelectedDay() {
    const token = ++calendar.dayToken;
    const day = calendar.selected;
    calendar.dayLoaded = false;
    calendar.dayOperations = [];
    renderCalendar();
    try {
        const ops = await api(`/api/operations?from=${day}&to=${day}&limit=500`);
        if (token !== calendar.dayToken) return;
        calendar.dayOperations = ops;
        calendar.dayLoaded = true;
        renderCalendar();
    } catch (e) {
        if (token === calendar.dayToken) await alertAsync(e.message);
    }
}

/** Точки под числом: красная — были расходы, зелёная — доходы, серая — только переводы. */
function renderDots(summary) {
    const dots = [];
    if (summary.expense > 0) dots.push(el('span', { className: 'dot expense' }));
    if (summary.income > 0) dots.push(el('span', { className: 'dot income' }));
    if (dots.length === 0 && summary.count > 0) dots.push(el('span', { className: 'dot transfer' }));
    return dots;
}

/** Блок "Доходы | Расходы" (для месяца и для дня). */
function renderIncomeExpense(income, expense, incomeLabel, expenseLabel) {
    return el('div', { className: 'summary' },
        el('div', {},
            el('div', { className: 'hint' }, incomeLabel),
            el('div', { className: 'amount income' }, '+' + money.format(income))),
        el('div', {},
            el('div', { className: 'hint' }, expenseLabel),
            el('div', { className: 'amount' }, '−' + money.format(expense))),
    );
}

function renderCalendar() {
    // Ответ сервера мог прийти, когда пользователь уже ушёл с календаря (например, в форму).
    // Тогда не рисуем — иначе перехватили бы MainButton у текущего экрана.
    if (screens.calendar.hidden) return;

    const { year, month, selected } = calendar;
    const today = localIsoDate(0);
    const backToCalendar = () => openCalendar();

    // ---- Заголовок "Ноябрь 2026" со стрелками ----
    const monthName = new Intl.DateTimeFormat('ru-RU', { month: 'long' }).format(new Date(year, month, 1));
    const header = el('div', { className: 'cal-header' },
        el('button', { className: 'cal-nav', onclick: () => shiftMonth(-1), ariaLabel: 'Предыдущий месяц' }, '‹'),
        el('div', { className: 'cal-title' }, `${monthName[0].toUpperCase()}${monthName.slice(1)} ${year}`),
        el('button', { className: 'cal-nav', onclick: () => shiftMonth(1), ariaLabel: 'Следующий месяц' }, '›'),
    );

    // ---- Сетка: дни недели + пустые клетки до 1-го числа + дни месяца ----
    // getDay(): 0 = воскресенье. Сдвигаем так, чтобы неделя начиналась с понедельника.
    const firstWeekday = (new Date(year, month, 1).getDay() + 6) % 7;
    const cells = [
        ...WEEKDAYS.map(w => el('div', { className: 'cal-weekday' }, w)),
        ...Array.from({ length: firstWeekday }, () => el('div')),
    ];
    for (let day = 1; day <= daysInMonth(year, month); day++) {
        const iso = isoDate(year, month, day);
        const summary = calendar.days.get(iso);
        // Строки 'YYYY-MM-DD' можно сравнивать как текст — порядок совпадает с порядком дат.
        const classes = ['cal-day',
            iso === today && 'today',
            iso === selected && 'selected',
            iso > today && 'future'].filter(Boolean).join(' ');
        cells.push(el('button', { className: classes, onclick: () => selectDay(iso) },
            el('span', {}, String(day)),
            el('span', { className: 'cal-dots' }, summary ? renderDots(summary) : [])));
    }

    // ---- Итоги месяца ----
    const monthDays = [...calendar.days.values()];
    const monthIncome = monthDays.reduce((s, d) => s + d.income, 0);
    const monthExpense = monthDays.reduce((s, d) => s + d.expense, 0);

    // ---- Выбранный день ----
    const [sy, sm, sd] = selected.split('-').map(Number);
    const dayTitle = new Intl.DateTimeFormat('ru-RU', { weekday: 'long', day: 'numeric', month: 'long' })
        .format(new Date(sy, sm - 1, sd));
    const daySummary = calendar.days.get(selected);

    let dayContent;
    if (!calendar.dayLoaded) dayContent = el('div', { className: 'empty' }, 'Загрузка…');
    else if (calendar.dayOperations.length === 0) dayContent = el('div', { className: 'empty' }, 'В этот день операций нет');
    else dayContent = el('div', { className: 'list' }, calendar.dayOperations.map(op =>
        renderOperationRow(op, () => openOperationForm(op, { goBack: backToCalendar }))));

    screens.calendar.replaceChildren(
        header,
        el('div', { className: 'cal-grid' }, cells),
        renderIncomeExpense(monthIncome, monthExpense, 'Доходы за месяц', 'Расходы за месяц'),
        el('div', { className: 'day-title' }, dayTitle),
        daySummary && (daySummary.income > 0 || daySummary.expense > 0)
            ? renderIncomeExpense(daySummary.income, daySummary.expense, 'Доходы', 'Расходы')
            : null,
        el('div', { style: 'margin-top: 12px' }, dayContent),
    );

    // Добавить операцию можно только не в будущем (сервер всё равно не пропустит)
    if (selected <= today) {
        const shortDate = new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'long' })
            .format(new Date(sy, sm - 1, sd));
        setMainButton(`Добавить на ${shortDate}`,
            () => openOperationForm(null, { date: selected, goBack: backToCalendar }));
    } else {
        setMainButton(null);
    }
}

// =====================================================================
//  Экран: статистика
// =====================================================================
const stats = {
    year: 0,
    month: 0,          // 1..12 — как на сервере (в отличие от JS Date, где 0..11)
    type: 'Expense',   // 'Expense' | 'Income'
    data: null,        // ответ /api/stats
    loading: false,
    token: 0,          // защита от гонки ответов, как в календаре
};

function monthName(year, month, format = 'long') {
    const name = new Intl.DateTimeFormat('ru-RU', { month: format }).format(new Date(year, month - 1, 1));
    return name[0].toUpperCase() + name.slice(1);
}

function isCurrentMonth(year, month) {
    const now = new Date();
    return year === now.getFullYear() && month === now.getMonth() + 1;
}

function openStats({ reset = false } = {}) {
    if (reset || stats.year === 0) {
        const now = new Date();
        stats.year = now.getFullYear();
        stats.month = now.getMonth() + 1;
    }
    showScreen('stats');
    setBackButton(openHome);
    setMainButton(null);
    loadStats();
}

function goToStatsMonth(year, month) {
    if (year === stats.year && month === stats.month) return;
    stats.year = year;
    stats.month = month;
    tg.HapticFeedback?.selectionChanged();
    loadStats();
}

function shiftStatsMonth(delta) {
    const d = new Date(stats.year, stats.month - 1 + delta, 1);
    goToStatsMonth(d.getFullYear(), d.getMonth() + 1);
}

async function loadStats() {
    const token = ++stats.token;
    stats.loading = true;
    renderStats(); // старые данные остаются на экране полупрозрачными — без мигания
    try {
        const data = await api(`/api/stats?year=${stats.year}&month=${stats.month}`);
        if (token !== stats.token) return;
        stats.data = data;
    } catch (e) {
        if (token === stats.token) await alertAsync(e.message);
    } finally {
        if (token === stats.token) {
            stats.loading = false;
            renderStats();
        }
    }
}

function renderStats() {
    if (screens.stats.hidden) return;

    const { year, month, type, data } = stats;
    const isExpense = type === 'Expense';

    const header = el('div', { className: 'cal-header' },
        el('button', { className: 'cal-nav', onclick: () => shiftStatsMonth(-1), ariaLabel: 'Предыдущий месяц' }, '‹'),
        el('div', { className: 'cal-title' }, `${monthName(year, month)} ${year}`),
        // В будущее листать незачем — там пусто
        isCurrentMonth(year, month)
            ? el('div', { style: 'width: 40px' })
            : el('button', { className: 'cal-nav', onclick: () => shiftStatsMonth(1), ariaLabel: 'Следующий месяц' }, '›'),
    );

    const typeSwitch = el('div', { className: 'segmented' },
        [['Expense', 'Расходы'], ['Income', 'Доходы']].map(([value, label]) => el('button', {
            type: 'button',
            className: value === type ? 'active' : '',
            onclick: () => {
                if (stats.type === value) return;
                stats.type = value;
                tg.HapticFeedback?.selectionChanged();
                renderStats(); // данные уже есть (сервер прислал и расходы, и доходы) — просто перерисовать
            },
        }, label)));

    // Данные ещё ни разу не загружались
    if (!data) {
        screens.stats.replaceChildren(header, typeSwitch, el('div', { className: 'empty' }, 'Загрузка…'));
        return;
    }

    const pick = totals => isExpense ? totals.expense : totals.income;

    screens.stats.replaceChildren(
        header,
        typeSwitch,
        el('div', { className: 'stats-body' + (stats.loading ? ' loading' : '') },
            renderStatsHero(data, isExpense, pick),
            el('div', { className: 'section-title' }, 'По категориям'),
            renderCategoryBars(isExpense ? data.expenseByCategory : data.incomeByCategory, isExpense),
            el('div', { className: 'section-title' }, `${isExpense ? 'Расходы' : 'Доходы'} за 6 месяцев`),
            renderTrend(data.trend, pick),
        ),
    );
}

/** Главное число месяца + сравнение с прошлым + сальдо. */
function renderStatsHero(data, isExpense, pick) {
    const current = pick(data.current);
    const previous = pick(data.previous);

    // Сравнение с прошлым месяцем. Направление показываем СТРЕЛКОЙ и словом, а не только цветом.
    // Для расходов рост — плохо, для доходов — хорошо.
    let delta;
    if (previous === 0) {
        delta = current === 0 ? 'Операций нет' : 'В прошлом месяце: 0';
    } else if (current === previous) {
        delta = 'Как в прошлом месяце';
    } else {
        const percent = Math.round(Math.abs(current - previous) / previous * 100);
        const up = current > previous;
        const good = isExpense ? !up : up;
        delta = [
            el('span', { className: (up ? 'up-' : 'down-') + (good ? 'good' : 'bad') },
                `${up ? '↑' : '↓'} ${percent}%`),
            ` к прошлому месяцу (${money.format(previous)})`,
        ];
    }

    const net = data.current.income - data.current.expense;

    return el('div', {},
        el('div', { className: 'hero-label' },
            `${isExpense ? 'Расходы' : 'Доходы'} за ${monthName(data.year, data.month).toLowerCase()}`),
        el('div', { className: 'hero-value' }, money.format(current)),
        el('div', { className: 'delta' }, delta),
        el('div', { className: 'delta', style: 'margin-top: 4px' },
            'Доходы − расходы: ',
            el('span', { className: 'amount ' + (net >= 0 ? 'income' : 'negative') },
                (net > 0 ? '+' : '') + money.format(net))),
    );
}

/**
 * Отсортированные горизонтальные полосы — одна на категорию, все одного цвета.
 * Длина полосы = доля категории от итога месяца (совпадает с подписанным %).
 */
function renderCategoryBars(items, isExpense) {
    if (items.length === 0)
        return el('div', { className: 'empty' }, isExpense ? 'Расходов за этот месяц нет' : 'Доходов за этот месяц нет');

    const total = items.reduce((s, c) => s + c.amount, 0);

    return el('div', { className: 'list' }, items.map(c => {
        const share = c.amount / total;
        const percentText = share < 0.01 ? '<1%' : `${Math.round(share * 100)}%`;
        return el('div', {
            className: 'cat-row',
            // Для экранных дикторов — вся строка одной фразой
            ariaLabel: `${c.name}: ${money.format(c.amount)}, ${percentText}`,
        },
            el('div', { className: 'cat-line' },
                el('span', { className: 'row-icon' }, categoryIcon(c.icon)),
                el('div', { className: 'row-main' }, c.name,
                    el('div', { className: 'cat-meta' }, `${c.count} опер.`)),
                el('span', { className: 'amount' }, money.format(c.amount))),
            el('div', { className: 'bar-line' },
                el('div', { className: 'bar-track' },
                    el('div', { className: 'bar-fill', style: `width: ${Math.max(share * 100, 1)}%` })),
                el('span', { className: 'bar-percent' }, percentText)),
        );
    }));
}

/**
 * Столбцы за 6 месяцев. Выбранный (последний) месяц — акцентным цветом, остальные — серым.
 * Подпись значения — только над выбранным столбцом (не над каждым — иначе каша).
 * Нажатие на столбец переключает всю статистику на этот месяц.
 */
function renderTrend(trend, pick) {
    const max = Math.max(...trend.map(pick), 0);
    const compact = new Intl.NumberFormat('ru-RU', {
        style: 'currency', currency: state.me.currency, notation: 'compact', maximumFractionDigits: 1,
    });

    return el('div', { className: 'trend' }, trend.map(m => {
        const value = pick(m);
        const isSelected = m.year === stats.year && m.month === stats.month;
        const heightPercent = max > 0 ? value / max * 100 : 0;
        const label = `${monthName(m.year, m.month)} ${m.year}: ${money.format(value)}`;

        return el('button', {
            className: 'trend-col' + (isSelected ? ' current' : ''),
            onclick: () => goToStatsMonth(m.year, m.month),
            title: label,      // всплывающая подсказка на компьютере
            ariaLabel: label,  // для экранных дикторов
        },
            el('div', { className: 'trend-value' }, isSelected && value > 0 ? compact.format(value) : ''),
            el('div', { className: 'trend-area' },
                // Ненулевое значение — минимум 2px, чтобы маленькая сумма не исчезла совсем
                value > 0 && el('div', { className: 'trend-bar', style: `height: max(2px, ${heightPercent}%)` })),
            el('div', { className: 'trend-label' }, monthName(m.year, m.month, 'short').replace('.', '')),
        );
    }));
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

        await reloadData();
        openHome();
    } catch (e) {
        screens.home.replaceChildren(el('p', { className: 'error' }, `Ошибка: ${e.message}`));
    }
}

init();
