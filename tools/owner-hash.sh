#!/usr/bin/env bash
# StampGen — генератор хеша ключа владельца.
# Читает ключ со скрытым вводом, печатает ТОЛЬКО SHA-256 (hex).
# Сам ключ нигде не сохраняется. См. docs/OWNER-ATTESTATION.md.
printf 'Введите ключ владельца (ввод скрыт): ' >&2
IFS= read -rs key
printf '\nSHA-256 ключа владельца:\n'
printf '%s' "$key" | sha256sum | cut -d' ' -f1
printf '\nВставьте это значение в StampGen/Services/OwnerGuard.cs (константа OwnerKeySha256).\n'
