select ifsapp.supplier_info_api.Get_Name(ifsapp.invoice_api.Get_Identity(aut.company,
                                                                         aut.invoice_id)) provider_name,
                                                                         inv.invoice_no,inv.objid,inv.gross_dom_amount,inv.invoice_recipient
  from ifsapp.POSTING_PROPOSAL_AUTH_multi AUT
 inner join ifsapp.POSTING_PROPOSAL_HEAD HE
    on he.invoice_id = aut.invoice_id
    inner join ifsapp.MAN_SUPP_INVOICE INV on inv.invoice_id=aut.invoice_id
 where aut.objstate = 'Unacknowledged'
   and he.message_id = 'AWAITAUTH'
 order by aut.invoice_id desc
