import type { ManifestDashboard } from '@umbraco-cms/backoffice/dashboard';

export const manifests: ManifestDashboard[] = [
  {
    type: 'dashboard',
    alias: 'UmbracoCommunity.FormsAuditTrail.Dashboard',
    name: 'Forms Audit Trail',
    elementName: 'forms-audit-dashboard',
    weight: 20,
    meta: {
      label: 'Forms Audit Trail',
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
