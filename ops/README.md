# MelonMQ sem Docker - pacote operacional

Este diretorio contem templates para rodar MelonMQ como servico do sistema sem Docker.

## Layout recomendado

Linux:

- Binarios: `/opt/melonmq`
- Configuracao: `/etc/melonmq`
- Dados: `/var/lib/melonmq`
- Logs: `/var/log/melonmq`
- Usuario: `melonmq`

macOS:

- Binarios: `/opt/melonmq`
- Configuracao: `/etc/melonmq`
- Dados: `/var/lib/melonmq`
- Logs: `/var/log/melonmq`
- LaunchDaemon: `/Library/LaunchDaemons/com.melonmq.broker.plist`

## Publicacao dos binarios

```bash
dotnet publish src/MelonMQ.Broker/MelonMQ.Broker.csproj \
  --configuration Release \
  --output /opt/melonmq
```

Copie a configuracao base:

```bash
sudo mkdir -p /etc/melonmq /var/lib/melonmq /var/log/melonmq
sudo cp src/MelonMQ.Broker/appsettings.Production.json /etc/melonmq/appsettings.Production.json
sudo cp ops/melonmq.env.example /etc/melonmq/melonmq.env
```

Edite `/etc/melonmq/melonmq.env` com portas, caminhos, API keys, usuarios, certificado TLS e chave de cluster.

Gere hashes PBKDF2 para `MelonMQ__Security__Users__*`:

```bash
scripts/hash-password.sh "senha-forte"
```

## Linux systemd

```bash
sudo useradd --system --home /var/lib/melonmq --shell /usr/sbin/nologin melonmq 2>/dev/null || true
sudo mkdir -p /opt/melonmq /etc/melonmq /var/lib/melonmq /var/log/melonmq
sudo chown -R melonmq:melonmq /var/lib/melonmq /var/log/melonmq
sudo cp ops/systemd/melonmq.service /etc/systemd/system/melonmq.service
sudo cp ops/logrotate/melonmq /etc/logrotate.d/melonmq
sudo systemctl daemon-reload
sudo systemctl enable --now melonmq
```

Comandos uteis:

```bash
sudo systemctl status melonmq
sudo journalctl -u melonmq -f
curl http://127.0.0.1:9090/health
```

## macOS launchd

```bash
sudo mkdir -p /opt/melonmq /etc/melonmq /var/lib/melonmq /var/log/melonmq
sudo cp ops/launchd/com.melonmq.broker.plist /Library/LaunchDaemons/com.melonmq.broker.plist
sudo launchctl bootstrap system /Library/LaunchDaemons/com.melonmq.broker.plist
sudo launchctl enable system/com.melonmq.broker
sudo launchctl kickstart -k system/com.melonmq.broker
```

Comandos uteis:

```bash
sudo launchctl print system/com.melonmq.broker
tail -f /var/log/melonmq/broker.out.log /var/log/melonmq/broker.err.log
curl http://127.0.0.1:9090/health
```

## Upgrade

1. Pare o servico.
2. Execute backup do `DataDirectory`.
3. Publique a nova versao em um diretorio temporario.
4. Troque `/opt/melonmq` de forma atomica quando possivel.
5. Suba o servico.
6. Valide `/health`, `/stats`, `/metrics` e consumo de uma fila duravel de teste.

Linux:

```bash
sudo systemctl stop melonmq
scripts/dr-backup.sh /var/lib/melonmq /var/backups/melonmq
dotnet publish src/MelonMQ.Broker/MelonMQ.Broker.csproj -c Release -o /tmp/melonmq-new
sudo rsync -a --delete /tmp/melonmq-new/ /opt/melonmq/
sudo systemctl start melonmq
```

macOS:

```bash
sudo launchctl bootout system /Library/LaunchDaemons/com.melonmq.broker.plist
scripts/dr-backup.sh /var/lib/melonmq /var/backups/melonmq
dotnet publish src/MelonMQ.Broker/MelonMQ.Broker.csproj -c Release -o /tmp/melonmq-new
sudo rsync -a --delete /tmp/melonmq-new/ /opt/melonmq/
sudo launchctl bootstrap system /Library/LaunchDaemons/com.melonmq.broker.plist
```

## Pacote de release sem Docker

```bash
scripts/release-package.sh
```

O pacote inclui binarios publicados, templates `ops/`, configuracao de observabilidade e checksum SHA-256 em `artifacts/release/`.

## Restore

Pare o servico antes de restaurar.

```bash
scripts/dr-restore.sh /var/backups/melonmq/melonmq-backup-YYYYMMDDTHHMMSSZ.tar.gz /var/lib/melonmq --force
```

Depois suba o servico e valide:

```bash
curl http://127.0.0.1:9090/health
curl http://127.0.0.1:9090/stats
```

Valide o fluxo backup/restore em ambiente temporario:

```bash
scripts/dr-validate.sh
```

## Validacao operacional

```bash
scripts/ops-validate.sh
```

Esse comando valida scripts, plist launchd, unit systemd quando `systemd-analyze` existir e executa uma restauracao DR temporaria.

## Observabilidade

Em producao, prefira Prometheus/Grafana gerenciados como servicos separados. O auto-start local (`MelonMQ:Observability:LocalStack`) deve permanecer desligado fora de Development.
