#!/bin/bash
# uso: send.sh <segundos> <comandos...>  (cada argumento = una linea)
D="$(dirname "$0")"; W=$1; shift
P=$(wc -c < "$D/uart.log"); printf '%s\n' "$@" > "$D/tx.txt"
timeout $W tail -f /dev/null; tail -c +$((P+1)) "$D/uart.log" | tr -d '\r'
