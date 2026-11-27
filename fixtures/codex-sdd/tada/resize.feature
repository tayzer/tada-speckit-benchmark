@REQ_INVENTORY_RESIZE
Feature: Resize an active reservation

  @AC_INVENTORY_RESIZE_01
  Scenario: Increase the reservation
    Given a reservation of 3 units
    When the reservation is resized to 5
    Then the response has status 200
    And the reservation has 5 units
    And 5 units are available

  @AC_INVENTORY_RESIZE_02
  Scenario: Reject an increase above available stock
    Given a reservation of 3 units
    When the reservation is resized to 12
    Then the response has status 409
    And the reservation has 3 units
    And 7 units are available
