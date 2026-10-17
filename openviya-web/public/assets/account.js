const API = '/api/v1';
const LOGIN_URL = 'https://api.openviya.online/api/v1/auth/telegram/login';
const ROOT_URL = 'https://openviya.online/';

const els = {
  pageMessage: document.querySelector('#page-message'),
  profileState: document.querySelector('#profile-state'),
  profileContent: document.querySelector('#profile-content'),
  profileAvatar: document.querySelector('#profile-avatar'),
  profileName: document.querySelector('#profile-name'),
  profileHandle: document.querySelector('#profile-handle'),
  telegramId: document.querySelector('#telegram-id'),
  subscriptionState: document.querySelector('#subscription-state'),
  subscriptionContent: document.querySelector('#subscription-content'),
  subscriptionLabel: document.querySelector('#subscription-label'),
  accessMark: document.querySelector('#access-mark'),
  subscriptionDetail: document.querySelector('#subscription-detail'),
  deviceLimit: document.querySelector('#device-limit'),
  trialUsed: document.querySelector('#trial-used'),
  paidConfirmed: document.querySelector('#paid-confirmed'),
  grantsBlock: document.querySelector('#grants-block'),
  grantList: document.querySelector('#grant-list'),
  refresh: document.querySelector('#refresh-button'),
  logout: document.querySelector('#logout-button'),
};

const stateNames = {
  ACTIVE: 'Активна',
  PENDING: 'Ожидает обработки',
  EXPIRED: 'Истекла',
  INACTIVE: 'Не оформлена',
  SUSPENDED: 'Приостановлена',
};

const grantNames = { TRIAL: 'Пробный доступ', PURCHASE: 'Покупка', PROMO: 'Промо' };
const grantStates = { PENDING: 'ожидает активации', ACTIVE: 'активен', REVOKED: 'отозван', SUPERSEDED: 'заменён' };
let requestGeneration = 0;
let isRefreshing = false;
let isLoggingOut = false;

function showMessage(message, tone = 'info') {
  els.pageMessage.textContent = message;
  els.pageMessage.dataset.tone = tone;
  els.pageMessage.hidden = false;
}

function clearMessage() {
  els.pageMessage.hidden = true;
  els.pageMessage.textContent = '';
  delete els.pageMessage.dataset.tone;
}

async function api(path, options = {}) {
  const controller = new AbortController();
  const timeout = window.setTimeout(() => controller.abort(), 12000);
  let response;
  try {
    response = await fetch(`${API}${path}`, {
      credentials: 'same-origin',
      cache: 'no-store',
      ...options,
      signal: controller.signal,
      headers: { Accept: 'application/json', ...options.headers },
    });
  } catch (cause) {
    const error = new Error(cause.name === 'AbortError' ? 'REQUEST_TIMEOUT' : 'NETWORK_ERROR');
    error.code = error.message;
    throw error;
  } finally {
    window.clearTimeout(timeout);
  }
  if (response.status === 401) {
    const error = new Error('UNAUTHORIZED');
    error.code = 'UNAUTHORIZED';
    throw error;
  }
  if (!response.ok) {
    let payload = {};
    try { payload = await response.json(); } catch { /* response may be empty */ }
    const error = new Error(payload.message || 'API_ERROR');
    error.code = payload.code || `HTTP_${response.status}`;
    error.status = response.status;
    throw error;
  }
  if (response.status === 204) return null;
  return response.json();
}

function dateTime(value) {
  if (!value) return null;
  const date = new Date(value);
  if (Number.isNaN(date.valueOf())) return null;
  return new Intl.DateTimeFormat('ru-RU', { dateStyle: 'long', timeStyle: 'short' }).format(date);
}

function duration(seconds) {
  const days = Math.round(Number(seconds) / 86400);
  if (days <= 0 || !Number.isFinite(days)) return 'срок не указан';
  const mod10 = days % 10;
  const mod100 = days % 100;
  const word = mod10 === 1 && mod100 !== 11 ? 'день' : mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14) ? 'дня' : 'дней';
  return `${days} ${word}`;
}

function renderProfile(profile) {
  const name = profile.displayName || (profile.username ? `@${profile.username}` : 'Аккаунт OpenViya');
  els.profileName.textContent = name;
  els.profileAvatar.textContent = [...name.replace(/^@/, '')].slice(0, 2).join('').toLocaleUpperCase('ru-RU') || 'OV';
  els.profileHandle.textContent = profile.username ? `@${profile.username}` : 'Имя пользователя не указано';
  els.telegramId.textContent = profile.telegramUserId || '—';
  els.profileState.hidden = true;
  els.profileContent.hidden = false;
}

function renderGrant(grant) {
  const li = document.createElement('li');
  li.className = 'grant-item';
  const detail = document.createElement('div');
  const title = document.createElement('strong');
  title.textContent = grantNames[grant.sourceType] || 'Начисление';
  const description = document.createElement('span');
  description.textContent = `${duration(grant.durationSeconds)} · ${grantStates[grant.state] || 'состояние не указано'}`;
  detail.append(title, description);
  const date = document.createElement('span');
  date.className = 'grant-date';
  const start = dateTime(grant.startsAt);
  const end = dateTime(grant.endsAt);
  date.textContent = start && end ? `${start} — ${end}` : end ? `до ${end}` : (dateTime(grant.createdAt) || '');
  li.append(detail, date);
  return li;
}

function renderSubscription(subscription) {
  const label = stateNames[subscription.state] || 'Состояние не указано';
  els.subscriptionLabel.textContent = label;
  els.subscriptionLabel.dataset.state = subscription.state || '';
  els.accessMark.dataset.active = String(Boolean(subscription.accessActive));

  const end = dateTime(subscription.endsAt);
  if (subscription.accessActive && end) {
    els.subscriptionDetail.textContent = `Подписка действует до ${end}.`;
  } else if (subscription.state === 'PENDING') {
    els.subscriptionDetail.textContent = 'Доступ ожидает обработки. Обновите страницу позже.';
  } else if (subscription.state === 'EXPIRED' && end) {
    els.subscriptionDetail.textContent = `Срок завершился ${end}`;
  } else if (subscription.state === 'INACTIVE') {
    els.subscriptionDetail.textContent = 'Активная подписка не найдена.';
  } else {
    els.subscriptionDetail.textContent = end ? `Дата окончания: ${end}` : 'Дата окончания пока не указана.';
  }

  els.deviceLimit.textContent = subscription.deviceLimit > 0 ? `${subscription.deviceLimit}` : '—';
  els.trialUsed.textContent = subscription.trialUsed ? 'Использован' : 'Не использован';
  els.paidConfirmed.textContent = subscription.paidPurchaseConfirmed ? 'Да' : 'Нет';

  const grants = Array.isArray(subscription.grants) ? [...subscription.grants].reverse() : [];
  els.grantList.replaceChildren(...grants.map(renderGrant));
  els.grantsBlock.hidden = grants.length === 0;
  els.subscriptionState.hidden = true;
  els.subscriptionContent.hidden = false;
}

function loginRequired() {
  requestGeneration += 1;
  isRefreshing = false;
  document.title = 'Вход требуется — OpenViya';
  els.profileState.replaceChildren();
  const text = document.createTextNode('Войдите через Telegram, чтобы открыть кабинет. ');
  const link = document.createElement('a');
  link.href = LOGIN_URL;
  link.textContent = 'Войти через Telegram';
  els.profileState.append(text, link);
  els.profileState.hidden = false;
  els.profileContent.hidden = true;
  els.subscriptionState.textContent = 'Данные подписки доступны после входа.';
  els.subscriptionState.hidden = false;
  els.subscriptionContent.hidden = true;
  els.refresh.disabled = true;
  els.logout.disabled = true;
}

async function refreshAccount() {
  if (isRefreshing || isLoggingOut) return;
  isRefreshing = true;
  const generation = ++requestGeneration;
  clearMessage();
  els.refresh.disabled = true;
  const [profileResult, subscriptionResult] = await Promise.allSettled([
    api('/me'),
    api('/subscription'),
  ]);

  if (generation !== requestGeneration || isLoggingOut) {
    isRefreshing = false;
    return;
  }

  const errors = [profileResult, subscriptionResult].filter((result) => result.status === 'rejected');
  if (errors.some((result) => result.reason.code === 'UNAUTHORIZED')) {
    loginRequired();
    return;
  }

  if (profileResult.status === 'fulfilled') {
    renderProfile(profileResult.value);
    document.title = 'Личный кабинет — OpenViya';
  } else {
    els.profileState.textContent = 'Не удалось загрузить профиль. Проверьте соединение и повторите попытку.';
    els.profileState.hidden = false;
    els.profileContent.hidden = true;
  }

  if (subscriptionResult.status === 'fulfilled') {
    renderSubscription(subscriptionResult.value);
  } else {
    els.subscriptionState.textContent = 'Не удалось загрузить подписку. Повторите попытку.';
    els.subscriptionState.hidden = false;
    els.subscriptionContent.hidden = true;
  }

  if (errors.length) showMessage('Не все данные кабинета удалось получить. Нажмите «Обновить», чтобы повторить запрос.', 'error');
  els.refresh.disabled = false;
  els.logout.disabled = false;
  isRefreshing = false;
}

async function logout() {
  if (isLoggingOut) return;
  isLoggingOut = true;
  requestGeneration += 1;
  els.refresh.disabled = true;
  els.logout.disabled = true;
  clearMessage();
  try {
    const csrf = await api('/auth/csrf');
    if (!csrf?.headerName || !csrf?.token) throw new Error('CSRF_TOKEN_MISSING');
    await api('/auth/logout', {
      method: 'POST',
      headers: { [csrf.headerName]: csrf.token },
    });
    window.location.assign(ROOT_URL);
  } catch (error) {
    if (error.code === 'UNAUTHORIZED') {
      loginRequired();
      return;
    }
    showMessage('Не удалось завершить сеанс. Повторите попытку.', 'error');
    els.logout.disabled = false;
    els.refresh.disabled = false;
    isLoggingOut = false;
    isRefreshing = false;
  }
}

els.refresh.addEventListener('click', refreshAccount);
els.logout.addEventListener('click', logout);
refreshAccount();
