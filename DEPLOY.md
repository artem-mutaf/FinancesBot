# Деплой на бесплатный сервер (Oracle Cloud Always Free + DuckDNS)

Итог: бот и Mini App работают круглосуточно на сервере, ноутбук не нужен.

```
Telegram ──HTTPS──► Caddy (сертификат Let's Encrypt) ──► app (ASP.NET) ──► db (PostgreSQL)
                                                                           ▲
                                                         backup (pg_dump раз в сутки)
```

---

## 0. Второй бот для разработки

Получать сообщения бота может только **одна** запущенная копия приложения.
Поэтому:
- **основной бот** — на сервере (им пользуешься);
- **тестовый бот** — на ноутбуке для разработки.

1. В Telegram: @BotFather → `/newbot` → например `my_finances_dev_bot`.
2. На ноутбуке переключить разработку на тестового бота:
   ```powershell
   dotnet user-secrets set "Bot:Token" "<токен ТЕСТОВОГО бота>" --project FinancesTelegramBot
   ```
3. Токен **основного** бота понадобится на сервере (шаг 5).

## 1. Адрес DuckDNS

1. https://www.duckdns.org → войти через GitHub.
2. Придумать имя, например `artem-finance` → адрес будет `artem-finance.duckdns.org`.
3. IP-адрес впишем после создания сервера (шаг 2).

## 2. Сервер в Oracle Cloud

1. https://cloud.oracle.com → **Sign Up**. Регион (Home Region) — **Germany Central (Frankfurt)** или
   **Netherlands Northwest (Amsterdam)**. Сменить потом нельзя. Карта нужна только для проверки.
2. SSH-ключ на ноутбуке (если ещё нет):
   ```powershell
   ssh-keygen -t ed25519
   Get-Content $HOME\.ssh\id_ed25519.pub   # это ПУБЛИЧНЫЙ ключ — его и вставляем в Oracle
   ```
3. **Compute → Instances → Create instance**:
   - Image: **Canonical Ubuntu 24.04**;
   - Shape: **Ampere → VM.Standard.A1.Flex**, 1–2 OCPU, 6–12 GB RAM
     (если "Out of capacity" — **VM.Standard.E2.1.Micro**, он тоже бесплатный);
   - SSH keys: **Paste public keys** → содержимое `id_ed25519.pub`;
   - Create. Записать **Public IP address**.
4. Открыть порты 80 и 443 в облачном файрволе:
   Instance → Subnet → **Security List** → **Add Ingress Rules**:
   Source CIDR `0.0.0.0/0`, TCP, Destination port `80`; то же для `443`.
5. В DuckDNS вписать Public IP в поле **current ip** → **update ip**.

## 3. Подключение и подготовка сервера

```powershell
ssh ubuntu@<Public IP>
```

Дальше — команды на сервере:

```bash
# Обновления системы
sudo apt update && sudo apt upgrade -y

# В образах Oracle порты закрыты ещё и ВНУТРИ системы (iptables) — открываем 80 и 443
sudo iptables -I INPUT 6 -m state --state NEW -p tcp --dport 80 -j ACCEPT
sudo iptables -I INPUT 6 -m state --state NEW -p tcp --dport 443 -j ACCEPT
sudo netfilter-persistent save

# Docker (официальный скрипт установки)
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker ubuntu     # чтобы запускать docker без sudo
exit                               # перезайти, чтобы группа применилась
```

Только для сервера с 1 ГБ памяти (E2.1.Micro) — файл подкачки:
```bash
sudo fallocate -l 2G /swapfile && sudo chmod 600 /swapfile
sudo mkswap /swapfile && sudo swapon /swapfile
echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
```

## 4. Код

```bash
git clone https://github.com/artem-mutaf/FinancesBot.git
cd FinancesBot
```

## 5. Настройки (.env)

```bash
cp .env.example .env
openssl rand -hex 24      # скопировать — это будет пароль базы
nano .env                 # вписать DOMAIN, BOT_TOKEN (основной бот), POSTGRES_PASSWORD
chmod 600 .env            # читать файл может только владелец
```

## 6. Запуск

```bash
docker compose up -d --build     # первая сборка — несколько минут
docker compose ps                # все 4 сервиса должны быть "running"/"healthy"
docker compose logs -f app       # логи приложения (выход — Ctrl+C)
```

Проверка: открыть `https://<DOMAIN>/api/health` в браузере, затем бота → кнопка «Финансы».

## Повседневное

| Задача | Команда (в папке FinancesBot) |
|---|---|
| Обновить после `git push` | `git pull && docker compose up -d --build` |
| Логи | `docker compose logs -f app` |
| Перезапустить | `docker compose restart app` |
| Список бэкапов | `ls -lh backups/` |
| Скачать бэкап на ноутбук | (на ноутбуке) `scp ubuntu@<IP>:FinancesBot/backups/<файл>.dump .` |
| Восстановить из бэкапа | `docker compose exec -T db pg_restore -U finances -d finances --clean --if-exists < backups/<файл>.dump` |
| Обновить систему | `sudo apt update && sudo apt upgrade -y` |

## Если что-то не так

- **Сертификат не получается** (в `docker compose logs caddy` ошибки ACME): проверь, что в DuckDNS
  правильный IP и что порты 80/443 открыты в **обоих** местах (Security List и iptables).
- **Бот молчит**: `docker compose logs app`. Ошибка `Conflict: terminated by other getUpdates request` —
  тем же ботом пользуется ещё одна копия приложения (например, на ноутбуке).
- **Oracle может забрать простаивающий бесплатный сервер.** Чтобы этого не случилось, аккаунт можно
  перевести в Pay As You Go (Billing → Upgrade): бесплатные ресурсы остаются бесплатными.
