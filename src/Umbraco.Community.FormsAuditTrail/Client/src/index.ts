import { umbExtensionsRegistry } from '@umbraco-cms/backoffice/extension-registry';
import { manifests } from './manifests.js';
import './dashboards/audit-dashboard.element.js';

manifests.forEach((m) => umbExtensionsRegistry.register(m));
