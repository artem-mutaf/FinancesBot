#!/bin/sh
# Ежедневная резервная копия базы. Хранятся копии за последние 14 дней.
#
# Формат -Fc ("custom"): сжатый, восстанавливается командой pg_restore.
# Восстановление (пример):
#   docker compose exec -T db pg_restore -U finances -d finances --clean --if-exists < backups/finances-....dump

while true; do
    file="/backups/finances-$(date +%Y-%m-%d_%H%M).dump"

    # Пишем во временный файл и переименовываем только при успехе —
    # чтобы оборванная копия не выглядела как нормальная.
    if pg_dump -Fc --no-owner -f "$file.tmp"; then
        mv "$file.tmp" "$file"
        echo "Бэкап готов: $file ($(du -h "$file" | cut -f1))"
    else
        echo "ОШИБКА: бэкап не удался"
        rm -f "$file.tmp"
    fi

    # Удаляем копии старше 14 дней
    find /backups -name 'finances-*.dump' -mtime +14 -delete

    sleep 86400   # сутки
done
