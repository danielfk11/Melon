# 🚀 Deployment em Produção - MelonMQ

Guia completo para usar MelonMQ em produção com segurança e performance.

## 📋 **Pré-requisitos de Sistema**

### **Servidor/VPS Requirements:**
- **.NET 8.0 Runtime** ou superior
- **RAM**: Mínimo 512MB, recomendado 2GB+
- **Storage**: SSD recomendado para persistência
- **Network**: Portas 5672 (TCP) e 8080 (HTTP) abertas
- **OS**: Linux (Ubuntu/CentOS), Windows Server, ou macOS

### **Verificar .NET Installation:**
```bash
dotnet --version
# Deve retornar 8.x.x ou superior
```

---

## 🛠️ **Instalação para Produção**

### **Método 1: Instalação Global (Recomendado)**
```bash
# 1. Clonar repositório
git clone https://github.com/danielfk11/MelonMQ.git
cd MelonMQ

# 2. Build em Release
dotnet build --configuration Release

# 3. Instalar como global tool
dotnet pack src/MelonMQ.Broker/MelonMQ.Broker.csproj --configuration Release --output ./dist
dotnet tool install --global --add-source ./dist MelonMQ.Broker

# 4. Verificar instalação
melonmq --version
```

### **Método 2: Self-Contained Deployment**
```bash
# Publicar com runtime incluído
dotnet publish src/MelonMQ.Broker \
  --configuration Release \
  --self-contained \
  --runtime linux-x64 \
  --output ./publish

# Executar
./publish/MelonMQ.Broker
```

---

## ⚙️ **Configuração de Produção**

### **1. Criar arquivo de configuração:**
```bash
sudo mkdir -p /etc/melonmq
sudo nano /etc/melonmq/appsettings.production.json
```

### **2. Configuração recomendada para produção:**
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "MelonMQ": {
    "TcpPort": 5672,
    "HttpPort": 8080,
    "DataDirectory": "/var/lib/melonmq/data",
    "BatchFlushMs": 50,
    "CompactionThresholdMB": 500,
    "ConnectionTimeout": 30000,
    "HeartbeatInterval": 15000,
    "MaxConnections": 1000,
    "MaxMessageSize": 10485760,
    "Security": {
      "RequireAuth": false,
      "AllowedOrigins": ["*"]
    }
  }
}
```

### **3. Criar diretórios necessários:**
```bash
sudo mkdir -p /var/lib/melonmq/data
sudo mkdir -p /var/log/melonmq
sudo chown -R $USER:$USER /var/lib/melonmq
sudo chown -R $USER:$USER /var/log/melonmq
```

---

## 🔧 **Configuração como Serviço (systemd)**

### **1. Criar service file:**
```bash
sudo nano /etc/systemd/system/melonmq.service
```

### **2. Conteúdo do service:**
```ini
[Unit]
Description=MelonMQ Message Broker
After=network.target

[Service]
Type=simple
User=melonmq
Group=melonmq
WorkingDirectory=/opt/melonmq
ExecStart=/usr/local/bin/melonmq --urls http://0.0.0.0:8080
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false
Restart=always
RestartSec=5
StandardOutput=journal
StandardError=journal

# Security settings
NoNewPrivileges=yes
PrivateTmp=yes
ProtectSystem=strict
ReadWritePaths=/var/lib/melonmq
ReadWritePaths=/var/log/melonmq

[Install]
WantedBy=multi-user.target
```

### **3. Configurar e iniciar:**
```bash
# Criar usuário dedicado
sudo useradd -r -s /bin/false melonmq

# Ajustar permissões
sudo chown -R melonmq:melonmq /var/lib/melonmq
sudo chown -R melonmq:melonmq /var/log/melonmq

# Ativar serviço
sudo systemctl daemon-reload
sudo systemctl enable melonmq
sudo systemctl start melonmq

# Verificar status
sudo systemctl status melonmq
```

---

## 🔒 **Segurança em Produção**

### **1. Firewall Configuration:**
```bash
# Ubuntu/Debian
sudo ufw allow 5672/tcp comment "MelonMQ TCP"
sudo ufw allow 8080/tcp comment "MelonMQ HTTP"

# CentOS/RHEL
sudo firewall-cmd --permanent --add-port=5672/tcp
sudo firewall-cmd --permanent --add-port=8080/tcp
sudo firewall-cmd --reload
```

### **2. Reverse Proxy (Nginx) para HTTP API:**
```nginx
# /etc/nginx/sites-available/melonmq
server {
    listen 80;
    server_name melonmq.yourdomain.com;
    
    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

### **3. SSL/TLS (Let's Encrypt):**
```bash
sudo certbot --nginx -d melonmq.yourdomain.com
```

---

## 📊 **Monitoramento e Logs**

### **1. Logs do sistema:**
```bash
# Ver logs em tempo real
sudo journalctl -u melonmq -f

# Ver logs das últimas 24h
sudo journalctl -u melonmq --since "24 hours ago"

# Ver apenas erros
sudo journalctl -u melonmq -p err
```

### **2. Health Check endpoints:**
```bash
# Status básico
curl http://localhost:8080/health

# Estatísticas detalhadas
curl http://localhost:8080/stats
```

### **3. Script de monitoramento simples:**
```bash
#!/bin/bash
# /usr/local/bin/melonmq-monitor.sh

HEALTH_URL="http://localhost:8080/health"
LOG_FILE="/var/log/melonmq/monitor.log"

if ! curl -s "$HEALTH_URL" | grep -q "healthy"; then
    echo "$(date): MelonMQ health check failed" >> "$LOG_FILE"
    sudo systemctl restart melonmq
    echo "$(date): MelonMQ restarted" >> "$LOG_FILE"
else
    echo "$(date): MelonMQ is healthy" >> "$LOG_FILE"
fi
```

### **4. Crontab para monitoramento:**
```bash
# Adicionar ao crontab
*/5 * * * * /usr/local/bin/melonmq-monitor.sh
```

---

## 🔧 **Backup e Restauração**

### **1. Backup dos dados:**
```bash
#!/bin/bash
# Script de backup
BACKUP_DIR="/backup/melonmq/$(date +%Y%m%d_%H%M%S)"
DATA_DIR="/var/lib/melonmq/data"

mkdir -p "$BACKUP_DIR"
sudo systemctl stop melonmq
sudo cp -r "$DATA_DIR" "$BACKUP_DIR/"
sudo systemctl start melonmq

echo "Backup criado em: $BACKUP_DIR"
```

### **2. Restauração:**
```bash
#!/bin/bash
# Script de restauração
BACKUP_PATH="$1"
DATA_DIR="/var/lib/melonmq/data"

if [ -z "$BACKUP_PATH" ]; then
    echo "Uso: $0 <caminho_do_backup>"
    exit 1
fi

sudo systemctl stop melonmq
sudo rm -rf "$DATA_DIR"
sudo cp -r "$BACKUP_PATH/data" "$DATA_DIR"
sudo chown -R melonmq:melonmq "$DATA_DIR"
sudo systemctl start melonmq

echo "Dados restaurados de: $BACKUP_PATH"
```

---

## 🚀 **Performance Tuning**

### **1. Configurações do sistema:**
```bash
# Aumentar limites de arquivos abertos
echo "melonmq soft nofile 65536" | sudo tee -a /etc/security/limits.conf
echo "melonmq hard nofile 65536" | sudo tee -a /etc/security/limits.conf

# Otimizações de rede
echo 'net.core.somaxconn = 1024' | sudo tee -a /etc/sysctl.conf
echo 'net.ipv4.tcp_max_syn_backlog = 1024' | sudo tee -a /etc/sysctl.conf
sudo sysctl -p
```

### **2. Configurações do MelonMQ para high-throughput:**
```json
{
  "MelonMQ": {
    "BatchFlushMs": 10,
    "MaxConnections": 5000,
    "MaxMessageSize": 52428800,
    "CompactionThresholdMB": 1000
  }
}
```

---

## 🔍 **Troubleshooting**

### **Problemas comuns:**

1. **Porta já em uso:**
   ```bash
   sudo netstat -tulpn | grep :5672
   sudo systemctl stop melonmq
   ```

2. **Permissões de arquivo:**
   ```bash
   sudo chown -R melonmq:melonmq /var/lib/melonmq
   sudo chmod -R 755 /var/lib/melonmq
   ```

3. **Out of memory:**
   ```bash
   # Verificar uso de memória
   ps aux | grep melonmq
   # Adicionar swap se necessário
   sudo fallocate -l 2G /swapfile
   sudo chmod 600 /swapfile
   sudo mkswap /swapfile
   sudo swapon /swapfile
   ```

4. **Performance lenta:**
   ```bash
   # Verificar I/O do disco
   sudo iotop
   # Verificar se SSD está sendo usado
   lsblk -o NAME,ROTA
   ```

---

## 📞 **Suporte**

Para problemas em produção:
1. **GitHub Issues**: https://github.com/danielfk11/MelonMQ/issues
2. **Logs**: Sempre incluir logs do `journalctl -u melonmq`
3. **Stats**: Incluir output de `/stats` endpoint
4. **System Info**: `uname -a`, `dotnet --info`

---

**MelonMQ** - Production ready! 🍈