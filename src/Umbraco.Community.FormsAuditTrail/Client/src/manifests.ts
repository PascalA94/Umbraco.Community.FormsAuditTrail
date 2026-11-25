import type { ManifestDashboard } from '@umbraco-cms/backoffice/dashboard';

export const manifests: ManifestDashboard[] = [
  {
    type: 'dashboard',
    alias: 'UmbracoCommunity.FormsAuditTrail.Dashboard',
    name: 'Audit',
    elementName: 'forms-audit-root',
    weight: 20,
    meta: {
      label: 'Audit',
      pathname: 'forms-audit-trail',
    },
    conditions: [
      {
        alias: 'Umb.Condition.SectionAlias',
        match: 'Umb.Section.Forms',
      },
    ],
  },
];
