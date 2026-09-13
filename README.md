# Sarafan Core

[![ci](https://github.com/sara-fan/sarafan.core/actions/workflows/ci.yml/badge.svg)](https://github.com/sara-fan/sarafan.core/actions/workflows/ci.yml)
[![publish](https://github.com/sara-fan/sarafan.core/actions/workflows/publish.yml/badge.svg)](https://github.com/sara-fan/sarafan.core/actions/workflows/publish.yml)
[![codecov](https://codecov.io/gh/sara-fan/sarafan.core/graph/badge.svg?token=6m88MgqjbB)](https://codecov.io/gh/sara-fan/sarafan.core)

«Сарафан» — прототип сервиса помощи в покупке и доставке товаров из зарубежных интернет-магазинов. Sarafan Core — его серверная часть: API для клиентского приложения и рабочего места сотрудников. Сервис обеспечивает регистрацию и вход покупателей, управление профилями, отдельную авторизацию сотрудников и разграничение доступа по ролям.

Core разработан на ASP.NET Core (.NET 10), хранит данные в PostgreSQL и поставляется в Docker-контейнере. Он также управляет версиями юридических документов и согласиями пользователей, хранит обращения по обработке персональных данных и историю официального курса USD/RUB Банка России. Текущая версия использует демонстрационное подтверждение телефона; условия перехода к реальным заказам и платежам описаны в руководстве по развёртыванию.

- [Локальная разработка и установка в облаке](docs/environment-setup.md)
